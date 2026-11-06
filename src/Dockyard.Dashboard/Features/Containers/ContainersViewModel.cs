using PublishedPort = Dockyard.Dashboard.Store.PublishedPort;
using System.ComponentModel;
using System.Diagnostics;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class ContainersViewModel : Observable, ISearchable
{
    readonly ShellViewModel shell;
    readonly StatsSampler sampler;
    readonly DashboardSettings settings = DashboardSettings.Current;
    object? selectedRow;
    IDisposable? detail;
    bool rebuilding;

    public ContainersViewModel(ShellViewModel shell, EngineSession session)
    {
        this.shell = shell;
        Session = session;
        sampler = new StatsSampler(session);

        ToggleCommand = new Command<ContainerItem>(i => Act(i, c => i.IsRunning ? c.StopContainerAsync(i.Id) : c.StartContainerAsync(i.Id)));
        RestartCommand = new Command<ContainerItem>(i => Act(i, c => c.RestartContainerAsync(i.Id)));
        RemoveCommand = new Command<ContainerItem>(i => Act(i, c => c.RemoveContainerAsync(i.Id, force: i.IsRunning)));
        AskRemoveCommand = new Command<ContainerItem>(i =>
        {
            SelectedRow = i;
            if (Detail is ContainerDetailViewModel d) d.ConfirmRemove = true;
            return Task.CompletedTask;
        });
        ExecCommand = new Command<ContainerItem>(Exec);
        OpenPortCommand = new Command<PublishedPort>(OpenPort);
        ToggleProjectCommand = new Command<ProjectRow>(p => { Store.ToggleProject(p.Name); return Task.CompletedTask; });
        ProjectStartStopCommand = new Command<ProjectRow>(p => WithProject(p, d => d.StartOrStop()));
        ProjectRestartCommand = new Command<ProjectRow>(p => WithProject(p, d => d.RestartCommand.CanExecute(null) ? Run(d.RestartCommand) : Task.CompletedTask));
        ProjectDownCommand = new Command<ProjectRow>(p => WithProject(p, d => { d.AskDown(); return Task.CompletedTask; }));

        Store.SetRecents(settings.RecentCompose);
        Store.RowsChanging += () => rebuilding = true;
        Store.RowsChanged += () =>
        {
            rebuilding = false;
            RestoreSelection();
            Raise(nameof(IsEmpty));
            sampler.Update();
        };
        session.Connected += sampler.Update;
        session.PropertyChanged += OnSessionChanged;
    }

    public EngineSession Session { get; }
    public ContainerStore Store => Session.Containers;
    public bool IsEmpty => Session.IsConnected && Store.Rows.Count == 0;

    public Command<ContainerItem> ToggleCommand { get; }
    public Command<ContainerItem> RestartCommand { get; }
    public Command<ContainerItem> RemoveCommand { get; }
    public Command<ContainerItem> AskRemoveCommand { get; }
    public Command<ContainerItem> ExecCommand { get; }
    public Command<PublishedPort> OpenPortCommand { get; }
    public Command<ProjectRow> ToggleProjectCommand { get; }
    public Command<ProjectRow> ProjectStartStopCommand { get; }
    public Command<ProjectRow> ProjectRestartCommand { get; }
    public Command<ProjectRow> ProjectDownCommand { get; }

    public object? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (rebuilding || !Set(ref selectedRow, value)) return;
            ShowDetail(value);
        }
    }

    public ContainerItem? Selected => selectedRow as ContainerItem;
    public ProjectRow? SelectedProject => selectedRow as ProjectRow;

    public object? Detail
    {
        get => detail;
        private set { if (Set(ref detail, value as IDisposable)) Raise(nameof(HasDetail)); }
    }

    public bool HasDetail => Detail is not null;

    public void ApplySearch(string text) => Store.Filter = text;

    public void SetVisible(string id, bool visible) => sampler.SetVisible(id, visible);

    public void CloseDetail() => SelectedRow = null;

    public bool IsRecent(string project) => settings.RecentCompose.Any(r => r.Project == project);

    public void ForgetRecent(string project)
    {
        settings.RecentCompose.RemoveAll(r => r.Project == project);
        settings.Save();
        Store.SetRecents(settings.RecentCompose);
    }

    public async Task OpenComposeFile(string path)
    {
        var name = ComposeCli.DefaultProjectName(path);
        settings.AddRecent(path, name);
        settings.Save();
        Store.SetRecents(settings.RecentCompose);
        if (Store.FindProject(name) is { } row)
        {
            SelectedRow = row;
            if (Detail is ComposeDetailViewModel d) await d.Up();
        }
    }

    public void Shutdown()
    {
        detail?.Dispose();
        sampler.StopAll();
    }

    void ShowDetail(object? row)
    {
        switch (row)
        {
            case ContainerItem item when Detail is ContainerDetailViewModel d && d.Item == item:
            case ProjectRow p when Detail is ComposeDetailViewModel cd && cd.Project.Name == p.Name:
                return;
        }
        detail?.Dispose();
        Detail = row switch
        {
            ContainerItem item => new ContainerDetailViewModel(this, Session, item, shell.ShowError),
            ProjectRow p => new ComposeDetailViewModel(this, Session, p.Project, shell.ShowError),
            _ => null,
        };
    }

    void RestoreSelection()
    {
        object? row = Detail switch
        {
            ContainerDetailViewModel d => Store.Get(d.Item.Id),
            ComposeDetailViewModel cd => Store.FindProject(cd.Project.Name),
            _ => null,
        };
        if (row is ContainerItem item && !Store.Rows.Contains(item)) row = null;
        if (Detail is ComposeDetailViewModel compose && row is ProjectRow p) compose.Update(p.Project);

        if (row is not null)
        {
            selectedRow = row;
            Raise(nameof(SelectedRow));
        }
        else if (Detail is ContainerDetailViewModel d && Store.Get(d.Item.Id) is not null)
        {
            selectedRow = null;
            Raise(nameof(SelectedRow));
        }
        else if (Detail is not null) SelectedRow = null;
    }

    Task WithProject(ProjectRow row, Func<ComposeDetailViewModel, Task> action)
    {
        SelectedRow = row;
        return Detail is ComposeDetailViewModel d ? action(d) : Task.CompletedTask;
    }

    static Task Run(System.Windows.Input.ICommand command)
    {
        command.Execute(null);
        return Task.CompletedTask;
    }

    void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EngineSession.State))
        {
            Raise(nameof(IsEmpty));
            if (!Session.IsConnected)
            {
                sampler.StopAll();
                SelectedRow = null;
            }
        }
    }

    async Task Act(ContainerItem item, Func<DockerClient, Task> action)
    {
        if (Session.Client is not { } client) return;
        try { await action(client); }
        catch (Exception ex) { shell.ShowError($"{item.Name}: {ex.Message}"); }
    }

    Task Exec(ContainerItem item)
    {
        if (!item.IsRunning)
        {
            shell.ShowError($"{item.Name} is not running.");
            return Task.CompletedTask;
        }
        if (Features.Containers.ExecCommand.FindTerminal() is not { } terminal)
        {
            shell.ShowError("No terminal found. Set the TERMINAL environment variable to your terminal emulator.");
            return Task.CompletedTask;
        }
        var command = Features.Containers.ExecCommand.Build(Session.ContextName, item.Id, item.Name, Session.IsWindowsEngine, terminal, DockerCli.Path());
        try
        {
            var psi = new ProcessStartInfo(command.File) { UseShellExecute = false, CreateNoWindow = false };
            foreach (var a in command.Args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex)
        {
            shell.ShowError($"Could not open a terminal: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    Task OpenPort(PublishedPort port)
    {
        try { Process.Start(new ProcessStartInfo(port.Url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { shell.ShowError($"Could not open {port.Url}: {ex.Message}"); }
        return Task.CompletedTask;
    }
}

sealed class StatsSampler(EngineSession session)
{
    readonly Dictionary<string, CancellationTokenSource> streams = new();
    readonly HashSet<string> visible = [];
    bool updateQueued;

    public void SetVisible(string id, bool isVisible)
    {
        if (isVisible ? visible.Add(id) : visible.Remove(id)) QueueUpdate();
    }

    void QueueUpdate()
    {
        if (updateQueued) return;
        updateQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            updateQueued = false;
            Update();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Update()
    {
        var running = session.Containers.Items.Where(i => i.IsRunning).Select(i => i.Id).ToList();
        var wanted = session.IsConnected ? StatsSelection.Choose(running, visible) : [];

        foreach (var id in streams.Keys.Where(id => !wanted.Contains(id)).ToList()) Cancel(id);
        foreach (var id in wanted.Where(id => !streams.ContainsKey(id)))
        {
            var cts = new CancellationTokenSource();
            streams[id] = cts;
            _ = Pump(id, cts);
        }
    }

    public void StopAll()
    {
        foreach (var id in streams.Keys.ToList()) Cancel(id);
    }

    void Cancel(string id)
    {
        if (!streams.Remove(id, out var cts)) return;
        cts.Cancel();
        cts.Dispose();
        session.Containers.Get(id)?.ClearUsage();
    }

    async Task Pump(string id, CancellationTokenSource cts)
    {
        if (session.Client is not { } client) return;
        try
        {
            await foreach (var usage in client.StatsAsync(id, cts.Token))
                session.Containers.Get(id)?.ApplyUsage(usage);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (streams.TryGetValue(id, out var current) && current == cts) streams.Remove(id);
        }
    }
}
