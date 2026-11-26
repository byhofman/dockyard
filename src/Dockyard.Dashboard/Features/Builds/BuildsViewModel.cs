using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Builds;

sealed record StepRow(BuildStep Step, double Share)
{
    public string Name => Step.Name;
    public bool IsDone => Step.State == StepState.Done;
    public bool IsCached => Step.State == StepState.Cached;
    public bool IsFailed => Step.State is StepState.Failed or StepState.Canceled;
    public bool IsRunning => Step.State == StepState.Running;
    public bool IsInternal => Step.IsInternal;
    public string DurationText => IsCached ? "cached" : Step.Duration is { } d ? BuildHistory.Format(d) : IsFailed ? "failed" : "";
    public bool HasLines => Step.Lines.Count > 0;
    public string Lines => string.Join('\n', Step.Lines);
    public override string ToString() => Name;
}

// BuildKit's build history on the engine: every build with how it ended, and a build's steps with their time and logs.
sealed class BuildsViewModel : SelectableSection<BuildRecord>
{
    List<BuildRecord> all = [];
    BuildDetails? details;
    string? error;
    bool loadingSteps;

    public BuildsViewModel(ShellViewModel shell, EngineSession session) : base(shell, session)
    {
        RefreshCommand = new Command(Reload);
        RemoveCommand = new Command(() => { AskRemove(Picked); return Task.CompletedTask; }, () => SelectionCount > 0);
        RemoveRowCommand = new Command<BuildRecord>(b => { AskRemove([b]); return Task.CompletedTask; });
        session.Connected += () => _ = Reload();
        session.ResourceChanged += r => { if (r.Kind == RouteKind.Images) ScheduleReload(); };
        session.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(EngineSession.ContextName)) _ = Reload(); };
    }

    public Command RefreshCommand { get; }
    public Command RemoveCommand { get; }
    public Command<BuildRecord> RemoveRowCommand { get; }
    public ObservableCollection<StepRow> Steps { get; } = [];

    public bool LoadingSteps { get => loadingSteps; private set => Set(ref loadingSteps, value); }

    public string Hint =>
        Session.IsWindowsEngine ? "The Windows engine builds without BuildKit, which is what keeps a build history. Switch to Linux to see builds."
        : !Shell.Tools.HasBuildx ? "The build history comes from Docker Buildx, which is not installed. Add it in Tools."
        : error is not null ? error
        : all.Count == 0 ? "No builds yet. Every docker build on this engine shows up here, with its steps and logs."
        : "";

    public bool HasHint => Hint.Length > 0;
    public string? Error => details?.Error;
    public bool HasError => Error is not null;
    public string? Location => details is { Context: { } c } ? details.Dockerfile is { } f ? $"{f} in {c}" : c : null;
    public bool HasLocation => Location is not null;

    string Context => Session.ContextName.Length > 0 ? Session.ContextName : "default";

    public override void OnShown() => _ = Reload();

    public override async Task Reload()
    {
        await Shell.Tools.Refresh();
        if (Session.IsWindowsEngine || !Session.IsConnected || !Shell.Tools.HasBuildx)
        {
            all = [];
            error = null;
        }
        else
        {
            try
            {
                var (exit, output, stderr) = await CliRunner.Output(BuildHistory.ListArgs(Context));
                error = exit == 0 ? null : $"docker buildx history could not list the builds: {stderr.Trim()}";
                all = exit == 0 ? BuildHistory.ParseList(output) : [];
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                error = $"docker buildx is not available: {ex.Message}";
                all = [];
            }
        }
        ApplyFilter();
        Raise(nameof(Hint), nameof(HasHint));
        if (Selected is { IsRunning: true } running) await LoadDetails(running);
    }

    protected override object Key(BuildRecord row) => row.Ref;
    protected override IEnumerable<BuildRecord> All => all;
    public override string Summary => all.Count(b => b.IsFailed) is > 0 and var failed
        ? $"{Count(all.Count, "build", "builds")}, {failed} failed" : Count(all.Count, "build", "builds");
    protected override string?[] Words(BuildRecord row) => [row.Name, row.Ref, row.StatusText];
    protected override SearchResult Describe(BuildRecord row) => new(row.Name, $"{row.StatusText}  {row.StartedText}", row);

    protected override void OnSelectionChanged(BuildRecord? before)
    {
        RemoveCommand.Refresh();
        if (Selected?.Ref == before?.Ref && Selected is not null) return;
        details = null;
        LoadingSteps = false;
        Steps.Clear();
        RaiseDetails();
        if (Selected is { } build) _ = LoadDetails(build);
    }

    async Task LoadDetails(BuildRecord build)
    {
        try
        {
            if (Steps.Count == 0) LoadingSteps = true;
            var inspecting = CliRunner.Output(BuildHistory.InspectArgs(Context, build.Ref));
            var reading = CliRunner.Output(BuildHistory.LogsArgs(Context, build.Ref));
            var (inspect, logs) = (await inspecting, await reading);
            if (Selected?.Ref != build.Ref) return;
            details = inspect.Exit == 0 ? BuildHistory.ParseDetails(inspect.Output) : new BuildDetails(null, null, inspect.Error.Trim());
            var steps = BuildHistory.ParseSteps(logs.Error + "\n" + logs.Output);
            var total = build.Duration?.TotalSeconds is > 0 and var t ? t : steps.Sum(s => s.Duration?.TotalSeconds ?? 0);
            Replace(Steps, steps.Select(s => new StepRow(s, total > 0 ? Math.Min(1, (s.Duration?.TotalSeconds ?? 0) / total) : 0)));
            RaiseDetails();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Shell.ShowError($"Could not read build {build.Name}: {ex.Message}");
        }
        finally
        {
            if (Selected?.Ref == build.Ref) LoadingSteps = false;
        }
    }

    void RaiseDetails() => Raise(nameof(Error), nameof(HasError), nameof(Location), nameof(HasLocation));

    void AskRemove(IReadOnlyList<BuildRecord> builds)
    {
        if (builds.Count == 0) return;
        Ask(builds.Count == 1 ? $"Remove build {builds[0].Name} from the history?" : $"Remove {builds.Count} builds from the history?",
            "Only the record and its logs go; images it made stay.", "Remove",
            async d =>
            {
                await d.ForEach(builds, "Removing", b => b.Name, async b =>
                {
                    var (exit, _, stderr) = await CliRunner.Output(BuildHistory.RemoveArgs(Context, b.Ref));
                    if (exit != 0) throw new InvalidOperationException(stderr.Trim());
                });
                await Reload();
            });
    }
}
