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
        OpenFilesCommand = new Command(OpenFiles, () => Selected is not null);
        CloseFilesCommand = new Command(() => { CloseFiles(); return Task.CompletedTask; });
        OpenInExplorerCommand = new Command(() =>
        {
            if (Selected is { } v)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $@"\\wsl.localhost\dockyard\var\lib\docker\volumes\{v.Name}\_data") { UseShellExecute = false })?.Dispose();
            return Task.CompletedTask;
        }, () => Selected is not null);
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Volumes) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
    }

    public ObservableCollection<VolumeRow> Rows { get; } = [];
    public Command RemoveCommand { get; }
    public Command PruneCommand { get; }
    public Command OpenFilesCommand { get; }
    public Command CloseFilesCommand { get; }
    public Command OpenInExplorerCommand { get; }
    public Features.Files.FileBrowserViewModel? Files { get => files; private set { if (Set(ref files, value)) Raise(nameof(HasFiles)); } }
    public bool HasFiles => files is not null;
    public bool CanOpenInExplorer => OperatingSystem.IsWindows() && Session.Client?.IsWindows == false;

    public VolumeRow? Selected
    {
        get => selected;
        set
        {
            if (Rebuilding) return;
            if (!Set(ref selected, value)) return;
            Raise(nameof(HasSelection));
            RemoveCommand.Refresh();
            OpenFilesCommand.Refresh();
            OpenInExplorerCommand.Refresh();
            CloseFiles();
        }
    }

    public bool HasSelection => Selected is not null;

    // A container that never runs holds the volume, because the archive API works on containers. It goes away with the view.
    async Task OpenFiles()
    {
        if (Selected is not { } volume || Session.Client is not { } client) return;
        const string mount = "/volume";
        try
        {
            var image = await client.EnsureEmptyImageAsync();
            var holder = await client.CreateVolumeHolderAsync(image, volume.Name, mount);
            CloseFiles();
            Files = new Features.Files.FileBrowserViewModel(client, holder, mount, Shell.ShowError, async () =>
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
        catch (Exception e) when (e is DockerException or EngineUnavailableException) { Shell.ShowError($"{volume.DisplayName}: {e.Message}"); }
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
