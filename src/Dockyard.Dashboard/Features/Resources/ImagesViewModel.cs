using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Features.Files;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed class ImagesViewModel : SelectableSection<ImageRow>
{
    List<ImageSummary> images = [];
    List<ImageRow> all = [];
    FileBrowserViewModel? files;

    public ImagesViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        PullCommand = new Command(() => { AskPull(); return Task.CompletedTask; });
        RemoveCommand = new Command(() => { AskRemove(Picked); return Task.CompletedTask; }, () => SelectionCount > 0);
        RemoveRowCommand = new Command<ImageRow>(row => { AskRemove([row]); return Task.CompletedTask; });
        PruneCommand = new Command(() =>
        {
            var dangling = all.Count(r => r.IsDangling && !r.InUse);
            Ask("Prune dangling images?", $"Removes the untagged images no container uses ({Count(dangling, "image", "images")} now).", "Prune", async d =>
            {
                d.Report("Pruning images…");
                var r = await Client.PruneImagesAsync(danglingOnly: true);
                Shell.ShowInfo($"Reclaimed {Format.Bytes(r.SpaceReclaimed)}");
            });
            return Task.CompletedTask;
        });
        OpenFilesCommand = new Command(OpenFiles, () => Selected is not null);
        CloseFilesCommand = new Command(() => { CloseFiles(); return Task.CompletedTask; });
        TerminalCommand = new Command(() => OpenTerminal(Selected), () => Selected is not null);
        TerminalRowCommand = new Command<ImageRow>(OpenTerminal);
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Images) ScheduleReload(); };
        session.Containers.RowsChanged += Recompute;
    }

    public Command PullCommand { get; }
    public Command RemoveCommand { get; }
    public Command<ImageRow> RemoveRowCommand { get; }
    public Command PruneCommand { get; }
    public Command OpenFilesCommand { get; }
    public Command CloseFilesCommand { get; }
    public Command TerminalCommand { get; }
    public Command<ImageRow> TerminalRowCommand { get; }
    public FileBrowserViewModel? Files { get => files; private set { if (Set(ref files, value)) Raise(nameof(HasFiles)); } }
    public bool HasFiles => files is not null;

    protected override object Key(ImageRow row) => (row.Id, row.Reference);

    protected override void OnSelectionChanged(ImageRow? before)
    {
        RemoveCommand.Refresh();
        OpenFilesCommand.Refresh();
        TerminalCommand.Refresh();
        // A reload makes new rows for the same images; only another image closes the files.
        if (before?.Id != Selected?.Id) CloseFiles();
    }

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

    protected override void ApplyFilter() => ReplaceRows(all.Where(r => Matches(Filter, r.Reference, r.ShortId)));

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

    Task OpenTerminal(ImageRow? image)
    {
        if (image is null || Session.Client is not { } client) return Task.CompletedTask;
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

    // A tagged row removes that tag (and the image with its last one); an untagged row removes the image by its id.
    public static string RemoveTarget(ImageRow image) => image.IsDangling ? image.Id : image.Reference;

    void AskRemove(IReadOnlyList<ImageRow> rows)
    {
        if (rows.Count == 0) return;
        var title = rows.Count == 1 ? $"Remove {rows[0].Reference}?" : $"Remove {rows.Count} images?";
        var used = rows.Where(r => r.InUse).ToList();
        var lines = new List<string>
        {
            rows.Count == 1 ? $"{rows[0].Reference} ({rows[0].ShortId}, {rows[0].SizeText})." : string.Join(", ", rows.Take(6).Select(r => r.Reference)) + (rows.Count > 6 ? $" and {rows.Count - 6} more." : "."),
        };
        if (used.Count > 0) lines.Add($"{Count(used.Count, "of them is", "of them are")} used by containers; the engine refuses those unless they are forced and the containers are stopped.");
        if (Session.IsWindowsEngine) lines.Add("Windows images are large: removing one can take a minute or more.");
        Ask(title, string.Join(" ", lines), "Remove", d => d.ForEach(rows, "Removing", r => r.Reference,
                r => Client.RemoveImageAsync(RemoveTarget(r), force: d.Option)),
            option: "Force: also untag it everywhere and take it from stopped containers");
    }

    void AskPull()
    {
        var field = new DialogField("Image", placeholder: "nginx:alpine, or registry/name:tag");
        Shell.Open(new Dialog("Pull an image", "", "Pull", Pull, fields: [field]));
    }

    async Task Pull(Dialog dialog)
    {
        var reference = dialog.Field("Image").Value;
        dialog.Report($"Pulling {reference}…");
        var progress = new Progress<PullProgress>(p =>
            dialog.Report($"{reference}: {p.Status}", p.TotalBytes > 0 ? 100.0 * p.CurrentBytes / p.TotalBytes : null));
        await Client.PullImageAsync(reference, progress);
        Shell.ShowInfo($"Pulled {reference}");
    }
}
