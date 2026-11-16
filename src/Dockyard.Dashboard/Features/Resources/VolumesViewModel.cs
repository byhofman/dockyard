using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed class VolumesViewModel : SectionViewModel
{
    List<Volume> volumes = [];
    Dictionary<string, long>? sizes;
    List<VolumeRow> all = [];
    VolumeRow? selected;
    Features.Files.FileBrowserViewModel? files;

    public VolumesViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        RemoveCommand = new Command(() =>
        {
            if (Selected is { CanRemove: true } v)
                Ask($"Remove volume {v.DisplayName}? Its data is deleted.", "Remove", () => Run($"Removing {v.DisplayName}", c => c.RemoveVolumeAsync(v.Name)));
            return Task.CompletedTask;
        }, () => Selected is { CanRemove: true });
        PruneCommand = new Command(() =>
        {
            var unused = all.Count(v => !v.InUse);
            Ask($"Remove {unused} unused volume(s) and their data?", "Prune", () => Run("Pruning volumes", async c =>
            {
                var r = await c.PruneVolumesAsync();
                Shell.ShowInfo($"Reclaimed {Format.Bytes(r.SpaceReclaimed)}");
            }));
            return Task.CompletedTask;
        });
        OpenFilesCommand = new Command(() => OpenFiles(writable: false), () => Selected is not null);
        CloseFilesCommand = new Command(() => { CloseFiles(); return Task.CompletedTask; });
        OpenLocationCommand = new Command(() => { OpenLocation(); return Task.CompletedTask; }, () => Location?.Path is not null);
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Volumes) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
    }

    public ObservableCollection<VolumeRow> Rows { get; } = [];
    public Command RemoveCommand { get; }
    public Command PruneCommand { get; }
    public Command OpenFilesCommand { get; }
    public Command CloseFilesCommand { get; }
    public Command OpenLocationCommand { get; }
    public Features.Files.FileBrowserViewModel? Files { get => files; private set { if (Set(ref files, value)) Raise(nameof(HasFiles)); } }
    public bool HasFiles => files is not null;
    public HostOs Host { get; init; } = VolumeLocation.CurrentHost;
    public VolumeLocation? Location => Selected is { } v && Session.Client is { } c
        ? VolumeLocation.For(Host, Session.ContextName, c.Endpoint.Kind, c.IsWindows, v.Name, v.Driver, v.Mountpoint)
        : null;
    public bool CanOpenLocation => Location?.Path is not null;
    public string OpenLocationText => Host switch { HostOs.Windows => "Open in Explorer", HostOs.MacOS => "Show in Finder", _ => "Open folder" };

    public VolumeRow? Selected
    {
        get => selected;
        set
        {
            if (Rebuilding) return;
            var before = selected;
            if (!Set(ref selected, value)) return;
            Raise(nameof(HasSelection), nameof(Location), nameof(CanOpenLocation));
            RemoveCommand.Refresh();
            OpenFilesCommand.Refresh();
            OpenLocationCommand.Refresh();
            // A reload makes new rows for the same volumes; only another volume closes the files.
            if (before?.Name != value?.Name) CloseFiles();
        }
    }

    public bool HasSelection => Selected is not null;

    const string Mount = "/volume";

    // A container that never runs holds the volume, because the archive API works on containers. It goes away with the
    // view. The volume is mounted read-only until changes are asked for, so looking can never change it.
    async Task OpenFiles(bool writable)
    {
        if (Selected is not { } volume || Session.Client is not { } client) return;
        try
        {
            var (holder, release) = await Hold(client, volume.Name, writable);
            CloseFiles();
            var source = new Features.Files.ContainerFileSource(client, holder, Mount, writable, release);
            Files = new Features.Files.FileBrowserViewModel(source, Shell.ShowError, () => OpenFiles(writable: true));
        }
        catch (Exception e) when (e is DockerException or EngineUnavailableException) { Shell.ShowError($"{volume.DisplayName}: {e.Message}"); }
    }

    static async Task<(string Holder, Func<Task> Release)> Hold(DockerClient client, string volume, bool writable)
    {
        var image = await client.EnsureEmptyImageAsync();
        var holder = await client.CreateVolumeHolderAsync(image, volume, Mount, readOnly: !writable);
        return (holder, async () =>
        {
            try
            {
                await client.RemoveContainerAsync(holder, force: true);
                await client.RemoveImageAsync(image);
            }
            // Another holder may still use the image; and the engine may have stopped, which took the holder with it.
            catch (Exception e) when (e is DockerException or EngineUnavailableException) { }
        });
    }

    void OpenLocation()
    {
        if (Location?.Path is not { } path || Selected is not { } volume) return;
        if (!Features.Files.FileManager.CanOpen(path))
        {
            Shell.ShowError(Host == HostOs.Linux
                ? $"{path} belongs to root, so it cannot be opened as you. Export a copy instead."
                : $"{path} cannot be opened as you: it is the engine's own folder. Export a copy instead.");
            return;
        }
        try { Features.Files.FileManager.Open(path); }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { Shell.ShowError($"{volume.DisplayName}: {e.Message}"); }
    }

    // Into a new folder inside the one picked, never over anything already there; then shown in the file manager.
    public async Task Export(string parent)
    {
        if (Selected is not { } volume || Session.Client is not { } client) return;
        var name = volume.DisplayName.TrimEnd('…');
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim('.').Length == 0) name = "volume";
        var target = Path.Combine(parent, name);
        for (var n = 2; Directory.Exists(target) || File.Exists(target); n++) target = Path.Combine(parent, $"{name} ({n})");
        await Run($"Exporting {volume.DisplayName}", async () =>
        {
            var (holder, release) = await Hold(client, volume.Name, writable: false);
            try
            {
                using var response = await client.GetArchiveAsync(holder, Mount + "/");
                await using var tar = await response.Content.ReadAsStreamAsync();
                var result = await FileExport.ExtractAsync(tar, target);
                var skipped = result.Skipped > 0 ? $"; {result.Skipped} links or special files left out" : "";
                Shell.ShowInfo($"Exported {result.Files} files ({Format.Bytes(result.Bytes)}) to {target}{skipped}");
            }
            finally { await release(); }
            Features.Files.FileManager.Open(target);
        });
    }

    void CloseFiles()
    {
        files?.Dispose();
        Files = null;
    }

    public override Task Reload() => SafeReload(async c =>
    {
        volumes = await c.ListVolumesAsync();
        Recompute();
        var df = await c.DiskUsageAsync();
        sizes = (df.Volumes ?? []).Where(v => v.UsageData is not null).ToDictionary(v => v.Name, v => v.UsageData!.Size);
        Recompute();
    });

    void Recompute()
    {
        all = VolumeRows.Rows(volumes, Session.Containers.Items, sizes);
        ApplyFilter();
    }

    protected override void ApplyFilter()
    {
        var name = Selected?.Name;
        Replace(Rows, all.Where(r => Matches(Filter, r.Name, r.Driver, r.UsageText)));
        Selected = Rows.FirstOrDefault(r => r.Name == name);
        Raise(nameof(Selected), nameof(HasSelection));
    }
}
