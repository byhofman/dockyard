using PublishedPort = Dockyard.Dashboard.Store.PublishedPort;
using System.Collections.ObjectModel;
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
    bool syncing;
    HashSet<string> pickedKeys = [];
    string? busy;

    public ContainersViewModel(ShellViewModel shell, EngineSession session)
    {
        this.shell = shell;
        Session = session;
        sampler = new StatsSampler(session);

        ToggleCommand = new Command<ContainerItem>(i => i.IsSystem ? Task.CompletedTask : Act(i, c => i.IsRunning ? c.StopContainerAsync(i.Id) : c.StartContainerAsync(i.Id)));
        RestartCommand = new Command<ContainerItem>(i => i.IsSystem ? Task.CompletedTask : Act(i, c => c.RestartContainerAsync(i.Id)));
        AskRemoveCommand = new Command<ContainerItem>(i => { if (!i.IsSystem) AskRemove([i]); return Task.CompletedTask; });
        ExecCommand = new Command<ContainerItem>(Exec);
        OpenPortCommand = new Command<PublishedPort>(OpenPort);
        ToggleProjectCommand = new Command<ProjectRow>(p => { Store.ToggleProject(p.Name); return Task.CompletedTask; });
        ProjectStartStopCommand = new Command<ProjectRow>(p => WithProject(p, d => d.StartOrStop()));
        ProjectRestartCommand = new Command<ProjectRow>(p => WithProject(p, d => d.RestartCommand.CanExecute(null) ? Run(d.RestartCommand) : Task.CompletedTask));
        ProjectDownCommand = new Command<ProjectRow>(p => { AskDown(p.Project); return Task.CompletedTask; });
        ToggleClusterCommand = new Command<ClusterRow>(k => { Store.ToggleProject(k.Key); return Task.CompletedTask; });
        ToggleSystemCommand = new Command<SystemRow>(_ => { Store.ToggleProject(SystemRow.Key); return Task.CompletedTask; });
        ClusterStartStopCommand = new Command<ClusterRow>(k => shell.Clusters?.StartOrStop(k.Cluster) ?? Task.CompletedTask);
        OpenClusterCommand = new Command<ClusterRow>(k => { shell.Reveal("Clusters", k.Cluster); return Task.CompletedTask; });

        StartPickedCommand = new Command(() => StartOrStopPicked(stop: false), () => PickedContainers.Any(c => !c.IsRunning) || PickedClusters.Any(c => !c.IsRunning));
        StopPickedCommand = new Command(() => StartOrStopPicked(stop: true), () => PickedContainers.Any(c => c.IsRunning) || PickedClusters.Any(c => c.Nodes.Any(n => n.IsRunning)));
        RestartPickedCommand = new Command(() => Bulk("Restarting", PickedContainers, (c, i) => c.RestartContainerAsync(i.Id)), () => PickedContainers.Count > 0);
        RemovePickedCommand = new Command(() => { AskRemove(PickedContainers); return Task.CompletedTask; }, () => PickedContainers.Count > 0);
        ClearSelectionCommand = new Command(() => { SelectedRow = null; return Task.CompletedTask; });

        SelectedItems.CollectionChanged += (_, _) => { if (!rebuilding && !syncing) OnPicked(); };
        Store.SetRecents(settings.RecentCompose);
        Store.ShowSystem = settings.ShowSystemContainers;
        Store.RowsChanging += () =>
        {
            rebuilding = true;
            pickedKeys = SelectedItems.Select(RowKey).ToHashSet();
        };
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
    public Command<ContainerItem> AskRemoveCommand { get; }
    public Command<ContainerItem> ExecCommand { get; }
    public Command<PublishedPort> OpenPortCommand { get; }
    public Command<ProjectRow> ToggleProjectCommand { get; }
    public Command<ProjectRow> ProjectStartStopCommand { get; }
    public Command<ProjectRow> ProjectRestartCommand { get; }
    public Command<ProjectRow> ProjectDownCommand { get; }
    public Command<ClusterRow> ToggleClusterCommand { get; }
    public Command<SystemRow> ToggleSystemCommand { get; }
    public Command<ClusterRow> ClusterStartStopCommand { get; }
    public Command<ClusterRow> OpenClusterCommand { get; }
    public Command StartPickedCommand { get; }
    public Command StopPickedCommand { get; }
    public Command RestartPickedCommand { get; }
    public Command RemovePickedCommand { get; }
    public Command ClearSelectionCommand { get; }

    public ObservableCollection<object> SelectedItems { get; } = [];
    public int SelectionCount => SelectedItems.Count;
    public bool HasMany => SelectionCount > 1;

    public string SelectionText
    {
        get
        {
            var n = PickedContainers.Count;
            return $"{SelectionCount} selected" + (SelectedItems.Any(r => r is ProjectRow) ? $" ({(n == 1 ? "1 container" : $"{n} containers")})" : "");
        }
    }

    public bool? AllSelected
    {
        get => SelectionCount == 0 ? false : SelectionCount == Store.Rows.Count ? true : null;
        set => Pick(value == true ? Store.Rows.ToList() : []);
    }

    // A picked project stands for all of its containers.
    public IReadOnlyList<ContainerItem> PickedContainers => SelectedItems
        .SelectMany(r => r switch
        {
            ContainerItem { IsSystem: false } c => [c],
            ProjectRow p => p.Project.Services.SelectMany(s => s.Containers),
            _ => [],
        })
        .DistinctBy(c => c.Id)
        .ToList();

    public bool ShowSystem
    {
        get => Store.ShowSystem;
        set
        {
            if (Store.ShowSystem == value) return;
            Store.ShowSystem = value;
            settings.ShowSystemContainers = value;
            settings.Save();
            Raise(nameof(ShowSystem));
        }
    }

    IReadOnlyList<KindCluster> PickedClusters => SelectedItems.OfType<ClusterRow>().Select(r => r.Cluster).ToList();

    // A cluster's nodes start control plane first, and its load balancer proxies go with it, so clusters go through Clusters.
    async Task StartOrStopPicked(bool stop)
    {
        if (shell.Clusters is { } clusters)
            foreach (var cluster in PickedClusters.Where(c => stop ? c.Nodes.Any(n => n.IsRunning) : !c.IsRunning))
                await (stop ? clusters.Stop(cluster) : clusters.Start(cluster));
        var containers = PickedContainers.Where(c => c.IsRunning == stop).ToList();
        if (containers.Count > 0)
            await Bulk(stop ? "Stopping" : "Starting", containers, (c, i) => stop ? c.StopContainerAsync(i.Id) : c.StartContainerAsync(i.Id));
    }

    public string? Busy { get => busy; private set { if (Set(ref busy, value)) Raise(nameof(IsBusy)); } }
    public bool IsBusy => busy is not null;

    public object? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (rebuilding) return;
            Pick(value is null ? [] : [value]);
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

    // The list's own rows, so hidden helpers stay hidden, but each only for what it says itself.
    public IEnumerable<SearchResult> Find(string text) => ContainerStore.BuildRows(Store.Items, text).Select(r => r switch
    {
        ProjectRow p when p.Name.Contains(text, StringComparison.OrdinalIgnoreCase) => new SearchResult(p.Name, p.Summary, p, "Compose projects", "IconCompose"),
        ContainerItem c when ContainerStore.Matches(c, text) => new SearchResult(c.Name, c.InProject ? $"{c.Project} · {c.Image}" : c.Image, c),
        _ => null,
    }).OfType<SearchResult>();

    // A container in a folded project unfolds it, so the row is there to pick.
    public void Reveal(object row)
    {
        if (row is ContainerItem { InProject: true } inProject) Store.Expand(inProject.Project);
        object? shown = row switch
        {
            ContainerItem c => Store.Rows.OfType<ContainerItem>().FirstOrDefault(r => r.Id == c.Id),
            ProjectRow p => Store.FindProject(p.Name),
            _ => null,
        };
        Pick(shown is null ? [] : [shown]);
    }

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

    static string RowKey(object row) => row switch
    {
        ContainerItem c => "c:" + c.Id,
        ProjectRow p => "p:" + p.Name,
        ClusterRow k => k.Key,
        SystemRow => SystemRow.Key,
        _ => "",
    };

    void Pick(IReadOnlyList<object> rows)
    {
        syncing = true;
        try
        {
            SelectedItems.Clear();
            foreach (var row in rows) SelectedItems.Add(row);
        }
        finally { syncing = false; }
        OnPicked();
    }

    void OnPicked()
    {
        selectedRow = SelectedItems.Count == 1 ? SelectedItems[0] : null;
        Raise(nameof(SelectedRow), nameof(SelectionCount), nameof(HasMany), nameof(SelectionText), nameof(AllSelected));
        foreach (var c in new[] { StartPickedCommand, StopPickedCommand, RestartPickedCommand, RemovePickedCommand }) c.Refresh();
        ShowDetail(selectedRow);
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
            ProjectRow p => new ComposeDetailViewModel(this, Session, p.Project, shell.ShowError, shell.Activity),
            _ => null,
        };
    }

    // The store makes new rows on every change: what was picked stays picked, and an open detail stays open while its
    // container exists, also when the filter hides it.
    void RestoreSelection()
    {
        var rows = Store.Rows.Where(r => pickedKeys.Contains(RowKey(r))).ToList();
        syncing = true;
        try
        {
            SelectedItems.Clear();
            foreach (var row in rows) SelectedItems.Add(row);
        }
        finally { syncing = false; }

        if (Detail is ComposeDetailViewModel compose && Store.FindProject(compose.Project.Name) is { } p) compose.Update(p.Project);
        var keepHidden = rows.Count == 0 && Detail is ContainerDetailViewModel d && Store.Get(d.Item.Id) is not null;
        if (keepHidden)
        {
            selectedRow = null;
            Raise(nameof(SelectedRow), nameof(SelectionCount), nameof(HasMany), nameof(SelectionText), nameof(AllSelected));
            return;
        }
        OnPicked();
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

    // Start, stop and restart need no question; the bar says how far it is, and failures come together in one toast.
    async Task Bulk(string verb, IReadOnlyList<ContainerItem> items, Func<DockerClient, ContainerItem, Task> action)
    {
        if (Session.Client is not { } client || items.Count == 0 || IsBusy) return;
        var failed = new List<string>();
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                Busy = items.Count == 1 ? $"{verb} {items[i].Name}…" : $"{verb} {i + 1} of {items.Count}: {items[i].Name}…";
                try { await action(client, items[i]); }
                catch (Exception ex) { failed.Add($"{items[i].Name}: {ex.Message}"); }
            }
        }
        finally { Busy = null; }
        if (failed.Count > 0) shell.ShowError(string.Join("\n", failed));
    }

    public void AskRemove(IReadOnlyList<ContainerItem> items)
    {
        if (items.Count == 0) return;
        var running = items.Count(i => i.IsRunning);
        var title = items.Count == 1 ? $"Remove {items[0].Name}?" : $"Remove {items.Count} containers?";
        var message = items.Count == 1
            ? (items[0].IsRunning ? "It is running and is stopped first. " : "") + "Its own changes to files go with it; volumes stay."
            : (running > 0 ? $"{(running == 1 ? "1 of them is" : $"{running} of them are")} running and stopped first. " : "") + "Their own changes to files go with them; volumes stay.";
        shell.Open(new Dialog(title, message, "Remove", d => d.ForEach(items, "Removing", i => i.Name,
            i => (Session.Client ?? throw new InvalidOperationException("The engine is not connected.")).RemoveContainerAsync(i.Id, force: i.IsRunning)), danger: true));
    }

    public void AskDown(ComposeProject project) => shell.Open(new Dialog($"Down {project.Name}?",
        "Stops and removes the project's containers and networks. Volumes and images stay.", "Down", d => ComposeDown(project, d), danger: true));

    async Task ComposeDown(ComposeProject project, Dialog dialog)
    {
        var context = Session.ContextName.Length > 0 ? Session.ContextName : "default";
        dialog.Report($"docker compose down {project.Name}…");
        var exit = await CliRunner.Run(ComposeCli.Args(context, project, "down"), line => { if (line.Trim().Length > 0) dialog.Report(line.Trim()); }, Session.IsWindowsEngine);
        if (exit != 0) throw new InvalidOperationException($"docker compose down failed for {project.Name} (exit code {exit}). {dialog.Progress}");
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
        try { command.Start(); }
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
