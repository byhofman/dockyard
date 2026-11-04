using System.ComponentModel;
using System.Diagnostics;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class ContainersViewModel : Observable, ISearchable
{
    readonly ShellViewModel shell;
    readonly StatsSampler sampler;
    object? selectedRow;
    ContainerDetailViewModel? detail;
    bool rebuilding;

    public ContainersViewModel(ShellViewModel shell, EngineSession session)
    {
        this.shell = shell;
        Session = session;
        sampler = new StatsSampler(session);

        ToggleCommand = new Command<ContainerItem>(i => Act(i, c => i.IsRunning ? c.StopContainerAsync(i.Id) : c.StartContainerAsync(i.Id)));
        RestartCommand = new Command<ContainerItem>(i => Act(i, c => c.RestartContainerAsync(i.Id)));
        RemoveCommand = new Command<ContainerItem>(i => Act(i, c => c.RemoveContainerAsync(i.Id, force: i.IsRunning)));
        ExecCommand = new Command<ContainerItem>(Exec);
        OpenPortCommand = new Command<PublishedPort>(OpenPort);

        Store.RowsChanging += () => rebuilding = true;
        Store.RowsChanged += () =>
        {
            rebuilding = false;
            if (detail is not null && Store.Get(detail.Item.Id) is { } item)
            {
                selectedRow = item;
                Raise(nameof(SelectedRow));
            }
            else if (detail is not null) SelectedRow = null;
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
    public Command<ContainerItem> ExecCommand { get; }
    public Command<PublishedPort> OpenPortCommand { get; }

    public object? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (rebuilding) return;
            if (value is GroupHeader)
            {
                Raise(nameof(SelectedRow));
                return;
            }
            if (!Set(ref selectedRow, value)) return;
            ShowDetail(value as ContainerItem);
        }
    }

    public ContainerItem? Selected => selectedRow as ContainerItem;

    public ContainerDetailViewModel? Detail
    {
        get => detail;
        private set { if (Set(ref detail, value)) Raise(nameof(HasDetail)); }
    }

    public bool HasDetail => Detail is not null;

    public void ApplySearch(string text) => Store.Filter = text;

    public void SetVisible(string id, bool visible) => sampler.SetVisible(id, visible);

    public void CloseDetail() => SelectedRow = null;

    public void Shutdown()
    {
        Detail?.Dispose();
        sampler.StopAll();
    }

    void ShowDetail(ContainerItem? item)
    {
        if (Detail?.Item == item) return;
        Detail?.Dispose();
        Detail = item is null ? null : new ContainerDetailViewModel(this, Session, item, shell.ShowError);
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
        var command = Features.Containers.ExecCommand.Build(Session.ContextName, item.Id, item.Name, Session.IsWindowsEngine,
            Features.Containers.ExecCommand.FindWindowsTerminal());
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
