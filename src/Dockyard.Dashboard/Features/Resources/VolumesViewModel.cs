using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed class VolumesViewModel : SelectableSection<VolumeRow>
{
    List<Volume> volumes = [];
    Dictionary<string, long>? sizes;
    List<VolumeRow> all = [];
    Features.Files.FileBrowserViewModel? files;
    int selectedTab;

    public VolumesViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        RemoveCommand = new Command(() => { AskRemove(Picked); return Task.CompletedTask; }, () => Picked.Any(v => v.CanRemove));
        RemoveRowCommand = new Command<VolumeRow>(v => { AskRemove([v]); return Task.CompletedTask; });
        PruneCommand = new Command(() =>
        {
            var unused = all.Count(v => !v.InUse);
            Ask("Prune unused volumes?", $"Removes {Count(unused, "volume", "volumes")} that no container uses, with their data.", "Prune", async d =>
            {
                d.Report("Pruning volumes…");
                var r = await Client.PruneVolumesAsync();
                Shell.ShowInfo($"Reclaimed {Format.Bytes(r.SpaceReclaimed)}");
            });
            return Task.CompletedTask;
        });
        OpenFilesCommand = new Command(() => { SelectedTab = FilesTab; return Task.CompletedTask; }, () => Selected is not null);
        OpenLocationCommand = new Command(() => { OpenLocation(); return Task.CompletedTask; }, () => Location?.Path is not null);
        session.Connected += () => _ = Reload();
        Usage.Changed += ApplyFilter;
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Volumes) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
    }

    public Command RemoveCommand { get; }
    public Command<VolumeRow> RemoveRowCommand { get; }
    public Command PruneCommand { get; }
    public Command OpenFilesCommand { get; }
    public Command OpenLocationCommand { get; }
    public Features.Files.FileBrowserViewModel? Files { get => files; private set { if (Set(ref files, value)) Raise(nameof(HasFiles)); } }
    public bool HasFiles => files is not null;

    public const int FilesTab = 1;

    // Details first; the files load only when their tab is picked, as holding a volume starts a container.
    public int SelectedTab
    {
        get => selectedTab;
        set
        {
            if (!Set(ref selectedTab, value)) return;
            if (value == FilesTab && files is null) _ = OpenFiles(writable: false);
        }
    }
    public HostOs Host { get; init; } = VolumeLocation.CurrentHost;
    public VolumeLocation? Location => Selected is { } v && Session.Client is { } c
        ? VolumeLocation.For(Host, Session.ContextName, c.Endpoint.Kind, c.IsWindows, v.Name, v.Driver, v.Mountpoint)
        : null;
    public bool CanOpenLocation => Location?.Path is not null;
    public string OpenLocationText => Host switch { HostOs.Windows => "Open in Explorer", HostOs.MacOS => "Show in Finder", _ => "Open folder" };

    protected override object Key(VolumeRow row) => row.Name;

    protected override void OnSelectionChanged(VolumeRow? before)
    {
        Raise(nameof(Location), nameof(CanOpenLocation));
        RemoveCommand.Refresh();
        OpenFilesCommand.Refresh();
        OpenLocationCommand.Refresh();
        // A reload makes new rows for the same volumes; only another volume closes the files.
        if (before?.Name == Selected?.Name) return;
        CloseFiles();
        SelectedTab = 0;
    }

    void AskRemove(IReadOnlyList<VolumeRow> rows)
    {
        var removable = rows.Where(v => v.CanRemove).ToList();
        if (removable.Count == 0)
        {
            if (rows.Count > 0) Shell.ShowError(rows.Count == 1 ? $"{rows[0].DisplayName} is in use by {rows[0].UsageText}." : "All of them are in use by a container.");
            return;
        }
        var kept = rows.Count - removable.Count;
        var title = removable.Count == 1 ? $"Remove volume {removable[0].DisplayName}?" : $"Remove {removable.Count} volumes?";
        var message = (removable.Count == 1 ? "Its data is deleted." : "Their data is deleted.")
            + (kept > 0 ? $" {Count(kept, "other volume is", "other volumes are")} in use by a container and stays." : "");
        Ask(title, message, "Remove", d => d.ForEach(removable, "Removing", v => v.DisplayName, v => Client.RemoveVolumeAsync(v.Name)));
    }

    const string Mount = "/volume";

    // A container that never runs holds the volume, because the archive API works on containers. It goes away with the
    // view. The volume is mounted read-only until changes are asked for, so looking can never change it.
    async Task OpenFiles(bool writable)
    {
        if (Selected is not { } volume || Session.Client is not { } client) return;
        try
        {
            var (holder, release) = await Hold(client, volume.Name, writable);
            if (Selected?.Name != volume.Name)
            {
                await release();
                return;
            }
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
        if (Location?.Path is not { } root || Selected is not { } volume) return;
        var path = FolderFor(root, SelectedTab == FilesTab ? Files?.Directory : null);
        if (!Features.Files.FileManager.CanOpen(path)) path = root;
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

    // The folder the files viewer is in, under the volume's own folder on this computer.
    internal static string FolderFor(string location, string? browsing)
    {
        if (browsing is null || !browsing.StartsWith(Mount, StringComparison.Ordinal)) return location;
        var relative = browsing[Mount.Length..].Trim('/');
        return relative.Length == 0 ? location : Path.Combine([location, .. relative.Split('/')]);
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
        Usage.Count(all.Select(r => r.InUse));
        ApplyFilter();
    }

    public UsageFilter Usage { get; } = new();
    protected override bool Shows(VolumeRow row) => Usage.Shows(row.InUse);
    protected override void ShowEverything() => Usage.Current = UsageFilter.Kind.All;

    protected override IEnumerable<VolumeRow> All => all;
    protected override string?[] Words(VolumeRow row) => [row.Name, row.Driver, row.UsageText];
    protected override SearchResult Describe(VolumeRow row) => new(row.Name, row.UsageText, row);
}
