using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class ComposeDetailViewModel : Observable, IDisposable
{
    readonly ContainersViewModel owner;
    readonly EngineSession session;
    readonly Action<string> showError;
    ComposeProject project;
    ConfirmRequest? confirm;
    string? busy;
    bool showLogs;

    public ComposeDetailViewModel(ContainersViewModel owner, EngineSession session, ComposeProject project, Action<string> showError)
    {
        this.owner = owner;
        this.session = session;
        this.project = project;
        this.showError = showError;

        UpCommand = new Command(Up, () => !IsBusy && Project.CanUp);
        StopCommand = new Command(() => Cli("Stopping", "stop"), () => !IsBusy && Project.Total > 0);
        RestartCommand = new Command(() => Cli("Restarting", "restart"), () => !IsBusy && Project.Total > 0);
        PullCommand = new Command(() => Cli("Pulling", "pull"), () => !IsBusy && Project.CanUp);
        DownCommand = new Command(() => { AskDown(); return Task.CompletedTask; }, () => !IsBusy && Project.Total > 0);
        ForgetCommand = new Command(() => { owner.ForgetRecent(Project.Name); return Task.CompletedTask; });
        ToggleLogsCommand = new Command(() => { ShowLogs = !ShowLogs; return Task.CompletedTask; });
        CloseCommand = new Command(() => { owner.CloseDetail(); return Task.CompletedTask; });
        ServiceStartCommand = new Command<ComposeService>(s => ForService(s, (c, id) => c.StartContainerAsync(id)));
        ServiceStopCommand = new Command<ComposeService>(s => ForService(s, (c, id) => c.StopContainerAsync(id)));
        ServiceRestartCommand = new Command<ComposeService>(s => ForService(s, (c, id) => c.RestartContainerAsync(id)));
    }

    public ComposeProject Project
    {
        get => project;
        private set { if (Set(ref project, value)) { Raise(nameof(IsRecentOnly)); RefreshCommands(); } }
    }

    public bool IsRecentOnly => Project.Total == 0 && owner.IsRecent(Project.Name);
    public ObservableCollection<string> Output { get; } = [];
    public bool HasOutput => Output.Count > 0;
    public bool ShowOutput => HasOutput && !ShowLogs;
    public bool HasBottom => HasOutput || ShowLogs;
    public LogsViewModel Logs { get; } = new();

    public Command UpCommand { get; }
    public Command StopCommand { get; }
    public Command RestartCommand { get; }
    public Command PullCommand { get; }
    public Command DownCommand { get; }
    public Command ForgetCommand { get; }
    public Command ToggleLogsCommand { get; }
    public Command CloseCommand { get; }
    public Command<ComposeService> ServiceStartCommand { get; }
    public Command<ComposeService> ServiceStopCommand { get; }
    public Command<ComposeService> ServiceRestartCommand { get; }

    public ConfirmRequest? Confirm { get => confirm; private set { if (Set(ref confirm, value)) Raise(nameof(HasConfirm)); } }
    public bool HasConfirm => Confirm is not null;

    public string? Busy
    {
        get => busy;
        private set { if (Set(ref busy, value)) { Raise(nameof(IsBusy)); RefreshCommands(); } }
    }

    public bool IsBusy => Busy is not null;

    public bool ShowLogs
    {
        get => showLogs;
        set
        {
            if (!Set(ref showLogs, value)) return;
            Raise(nameof(ShowOutput), nameof(HasBottom));
            if (value) StartLogs();
            else Logs.Dispose();
        }
    }

    public void Update(ComposeProject updated) => Project = updated;

    public Task Up() => Cli("Starting", "up", "-d");

    public Task StartOrStop() => Project.IsRunning ? Cli("Stopping", "stop") : Project.CanUp ? Up() : Cli("Starting", "start");

    public void AskDown() => Confirm = new ConfirmRequest(
        $"Stop and remove the containers and networks of {Project.Name}? Volumes are kept.", "Down",
        new Command(async () => { Confirm = null; await Cli("Removing", "down"); }),
        new Command(() => { Confirm = null; return Task.CompletedTask; }));

    void RefreshCommands()
    {
        foreach (var c in new[] { UpCommand, StopCommand, RestartCommand, PullCommand, DownCommand }) c.Refresh();
    }

    async Task Cli(string what, params string[] verb)
    {
        if (IsBusy) return;
        var context = session.ContextName.Length > 0 ? session.ContextName : "default";
        var args = ComposeCli.Args(context, Project, verb);
        Output.Clear();
        AddOutput($"> docker {string.Join(' ', args)}");
        Busy = $"{what} {Project.Name}…";
        try
        {
            var exit = await CliRunner.Run(args, AddOutput);
            if (exit != 0) showError($"docker compose {verb[0]} failed for {Project.Name} (exit code {exit})");
        }
        catch (Exception ex)
        {
            showError($"Could not run docker compose: {ex.Message}");
        }
        finally
        {
            Busy = null;
        }
    }

    void AddOutput(string line)
    {
        Output.Add(line);
        if (Output.Count > 500) Output.RemoveAt(0);
        Raise(nameof(HasOutput), nameof(ShowOutput), nameof(HasBottom));
    }

    async Task ForService(ComposeService service, Func<DockerClient, string, Task> action)
    {
        if (session.Client is not { } client) return;
        try
        {
            foreach (var container in service.Containers) await action(client, container.Id);
        }
        catch (Exception ex)
        {
            showError($"{service.Name}: {ex.Message}");
        }
    }

    async void StartLogs()
    {
        if (session.Client is not { } client) return;
        var name = Project.Name;
        var sources = new List<LogSource>();
        foreach (var service in Project.Services)
            foreach (var container in service.Containers)
            {
                try
                {
                    var details = await client.InspectContainerAsync(container.Id);
                    sources.Add(new LogSource(container.Id, details.Config.Tty, service.Name));
                }
                catch (DockerException) { }
            }
        if (ShowLogs && Project.Name == name) Logs.Start(client, sources);
    }

    public void Dispose() => Logs.Dispose();
}
