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
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Volumes) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
    }

    public ObservableCollection<VolumeRow> Rows { get; } = [];
    public Command RemoveCommand { get; }
    public Command PruneCommand { get; }

    public VolumeRow? Selected
    {
        get => selected;
        set { if (Rebuilding) return; if (Set(ref selected, value)) { Raise(nameof(HasSelection)); RemoveCommand.Refresh(); } }
    }

    public bool HasSelection => Selected is not null;

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
