using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed class ImagesViewModel : SectionViewModel
{
    List<ImageSummary> images = [];
    List<ImageRow> all = [];
    ImageRow? selected;
    string pullReference = "";
    double pullPercent;
    string pullStatus = "";

    public ImagesViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        PullCommand = new Command(Pull, () => !IsBusy && PullReference.Trim().Length > 0);
        RemoveCommand = new Command(() => { AskRemove(); return Task.CompletedTask; }, () => Selected is not null);
        PruneCommand = new Command(() =>
        {
            Ask("Remove all dangling (untagged, unused) images?", "Prune", () => Run("Pruning images", async c =>
            {
                var r = await c.PruneImagesAsync(danglingOnly: true);
                Shell.ShowInfo($"Reclaimed {Format.Bytes(r.SpaceReclaimed)}");
            }));
            return Task.CompletedTask;
        });
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Images) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) PullCommand.Refresh(); };
    }

    public ObservableCollection<ImageRow> Rows { get; } = [];
    public Command PullCommand { get; }
    public Command RemoveCommand { get; }
    public Command PruneCommand { get; }

    public ImageRow? Selected
    {
        get => selected;
        set { if (Rebuilding) return; if (Set(ref selected, value)) { Raise(nameof(HasSelection)); RemoveCommand.Refresh(); } }
    }

    public bool HasSelection => Selected is not null;

    public string PullReference
    {
        get => pullReference;
        set { if (Set(ref pullReference, value ?? "")) PullCommand.Refresh(); }
    }

    public double PullPercent { get => pullPercent; private set => Set(ref pullPercent, value); }
    public string PullStatus { get => pullStatus; private set => Set(ref pullStatus, value); }

    public override Task Reload() => SafeReload(async c =>
    {
        images = await c.ListImagesAsync();
        Recompute();
    });

    void Recompute()
    {
        all = ImageUsage.Rows(images, Session.Containers.Items);
        ApplyFilter();
    }

    protected override void ApplyFilter()
    {
        var key = Selected is { } s ? (s.Id, s.Reference) : default;
        Replace(Rows, all.Where(r => Matches(Filter, r.Reference, r.ShortId)));
        Selected = Rows.FirstOrDefault(r => (r.Id, r.Reference) == key);
        Raise(nameof(Selected), nameof(HasSelection));
    }

    void AskRemove()
    {
        if (Selected is not { } image) return;
        var target = image.IsDangling ? image.Id : image.Reference;
        var note = image.InUse ? $" It is used by {image.UsageText}; the engine refuses if any of them still exist." : "";
        Ask($"Remove {image.Reference} ({image.ShortId})?{note}", "Remove",
            () => Run($"Removing {image.Reference}", c => c.RemoveImageAsync(target)));
    }

    async Task Pull()
    {
        var reference = PullReference.Trim();
        PullPercent = 0;
        PullStatus = "Starting…";
        await Run($"Pulling {reference}", async c =>
        {
            var progress = new Progress<PullProgress>(p =>
            {
                PullStatus = p.Status;
                PullPercent = p.TotalBytes > 0 ? 100.0 * p.CurrentBytes / p.TotalBytes : 0;
            });
            await c.PullImageAsync(reference, progress);
            PullReference = "";
            Shell.ShowInfo($"Pulled {reference}");
        });
        PullStatus = "";
        PullPercent = 0;
    }
}
