using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Files;
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
    FileBrowserViewModel? files;

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
        OpenFilesCommand = new Command(OpenFiles, () => Selected is not null);
        CloseFilesCommand = new Command(() => { CloseFiles(); return Task.CompletedTask; });
        TerminalCommand = new Command(OpenTerminal, () => Selected is not null);
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Images) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) PullCommand.Refresh(); };
    }

    public ObservableCollection<ImageRow> Rows { get; } = [];
    public Command PullCommand { get; }
    public Command RemoveCommand { get; }
    public Command PruneCommand { get; }
    public Command OpenFilesCommand { get; }
    public Command CloseFilesCommand { get; }
    public Command TerminalCommand { get; }
    public FileBrowserViewModel? Files { get => files; private set { if (Set(ref files, value)) Raise(nameof(HasFiles)); } }
    public bool HasFiles => files is not null;

    public ImageRow? Selected
    {
        get => selected;
        set
        {
            if (Rebuilding) return;
            var before = selected;
            if (!Set(ref selected, value)) return;
            Raise(nameof(HasSelection));
            RemoveCommand.Refresh();
            OpenFilesCommand.Refresh();
            TerminalCommand.Refresh();
            // A reload makes new rows for the same images; only another image closes the files.
            if (before?.Id != value?.Id) CloseFiles();
        }
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

    // A container of the image that never starts, read-only and without a network, is what the archive API reads from;
    // it goes away with the view.
    async Task OpenFiles()
    {
        if (Selected is not { } image || Session.Client is not { } client) return;
        try
        {
            var holder = await client.CreateImageHolderAsync(image.Id);
            CloseFiles();
            Files = new FileBrowserViewModel(new ImageFileSource(client, holder, image.Size), Shell.ShowError);
        }
        catch (Exception e) when (e is DockerException or EngineUnavailableException) { Shell.ShowError($"{image.Reference}: {e.Message}"); }
    }

    void CloseFiles()
    {
        files?.Dispose();
        Files = null;
    }

    Task OpenTerminal()
    {
        if (Selected is not { } image || Session.Client is not { } client) return Task.CompletedTask;
        if (ExecCommand.FindTerminal() is not { } terminal)
        {
            Shell.ShowError("No terminal found. Set the TERMINAL environment variable to your terminal emulator.");
            return Task.CompletedTask;
        }
        return Run($"Opening a terminal in {image.Reference}", async () =>
        {
            if (await ImageTerminal.FindShellAsync(client, image.Id) is not { } shell)
            {
                Shell.ShowError($"{image.Reference} has no shell (neither /bin/bash nor /bin/sh), so a terminal has nothing to run. Files still shows what is in it.");
                return;
            }
            var id = await ImageTerminal.CreateAsync(client, image.Id, shell);
            try { ExecCommand.BuildImageTerminal(Session.ContextName, id, image.Reference, terminal, DockerCli.Path()).Start(); }
            catch
            {
                await client.RemoveContainerAsync(id, force: true);
                throw;
            }
        });
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
