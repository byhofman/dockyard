using System.Collections.ObjectModel;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Clusters;

sealed class ClustersViewModel : SelectableSection<KindCluster>
{
    public static readonly IReadOnlyList<string> WorkerChoices = ["None", "1", "2", "3"];
    readonly DashboardSettings settings;
    List<KindCluster> all = [];
    string? kind;
    readonly HostKubeconfig kubeconfig;
    bool? controllerWanted;
    bool syncing;

    public ClustersViewModel(ShellViewModel shell, EngineSession session, HostKubeconfig? kubeconfig = null, DashboardSettings? settings = null) : base(shell, session)
    {
        this.kubeconfig = kubeconfig ?? HostKubeconfig.FromEnvironment();
        this.settings = settings ?? DashboardSettings.Current;
        CreateCommand = new Command(() => { AskCreate(); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine);
        DeleteCommand = new Command(() => { AskDelete(Picked); return Task.CompletedTask; }, () => !IsBusy && HasKind && SelectionCount > 0);
        DeleteRowCommand = new Command<KindCluster>(c => { AskDelete([c]); return Task.CompletedTask; });
        CopyKubeconfigCommand = new Command(CopyKubeconfig, () => HasKind && Selected is { IsRunning: true });
        UseContextCommand = new Command(UseContext, () => !IsBusy && HasKind && Selected is { IsRunning: true });
        DuplicateCommand = new Command(() => { if (Selected is { } c) AskDuplicate(c); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine && Selected is not null);
        RecreateCommand = new Command(() => { if (Selected is { } c) AskRecreate(c); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine && Selected is not null);
        ToggleCommand = new Command(() => Selected is { } c ? Toggle(c) : Task.CompletedTask, () => !IsBusy && Selected is not null);
        ToggleRowCommand = new Command<KindCluster>(c => IsBusy ? Task.CompletedTask : Toggle(c));
        OpenUrlCommand = new Command<PublishedPort>(p =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p.Url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Shell.ShowError($"Could not open {p.Url}: {ex.Message}"); }
            return Task.CompletedTask;
        });

        OpenSharedFolderCommand = new Command(() =>
        {
            if (SharedFolder is { } folder)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = false })?.Dispose(); }
                catch (Exception ex) { Shell.ShowError($"Could not open {folder}: {ex.Message}"); }
            return Task.CompletedTask;
        });

        session.Connected += () => { controllerWanted = null; _ = Reload(); };
        session.Containers.RowsChanged += () => _ = Reload();
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EngineSession.ContextName) or nameof(EngineSession.State)) Raise(nameof(NeedsLinuxEngine), nameof(Hint));
        };
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy)) RefreshCommands(); };
    }

    public ObservableCollection<LoadBalancer> LoadBalancerRows { get; } = [];
    public Command CreateCommand { get; }
    public Command DeleteCommand { get; }
    public Command<KindCluster> DeleteRowCommand { get; }
    public Command CopyKubeconfigCommand { get; }
    public Command UseContextCommand { get; }
    public Command DuplicateCommand { get; }
    public Command RecreateCommand { get; }
    public Command ToggleCommand { get; }
    public Command<KindCluster> ToggleRowCommand { get; }
    public Command<PublishedPort> OpenUrlCommand { get; }
    public Command OpenSharedFolderCommand { get; }
    public Command OpenSettingsCommand => Shell.SettingsCommand;

    // Set by the view, which has the clipboard.
    public Func<string, Task>? CopyText { get; set; }

    public bool HasKind => kind is not null;
    public bool NeedsLinuxEngine => Session.IsWindowsEngine;
    public bool HasLoadBalancers => LoadBalancerRows.Count > 0;

    public bool LoadBalancersEnabled
    {
        get => settings.LoadBalancers;
        set
        {
            if (settings.LoadBalancers == value) return;
            settings.LoadBalancers = value;
            settings.Save();
            Raise(nameof(LoadBalancersEnabled), nameof(LoadBalancerHint));
            _ = SyncController();
        }
    }

    public string LoadBalancerHint => LoadBalancersEnabled
        ? "A service of type LoadBalancer gets an address here, on this computer's 127.0.0.1."
        : "Off: LoadBalancer services stay pending. Turn on load balancers above the list.";

    public string Hint =>
        !HasKind ? "kind is not installed. Add it under Engines and settings, or run the setup again with --with kind."
        : NeedsLinuxEngine ? "kind runs on the Linux engine. Switch to Linux to see and create clusters."
        : Rows.Count == 0 ? "No clusters yet. kind runs each node as a container on this engine."
        : "";

    public bool HasHint => Hint.Length > 0;
    public string KubectlHint => Selected is { } c ? $"kubectl --context {c.Context} get nodes" : "";

    public bool HasSharedStorage => Selected?.HasSharedStorage == true;
    public string SharedStorageText => HasSharedStorage
        ? $"Storage class \"{SharedStorage.ClassName}\": ReadWriteMany, so pods on every node use a claim at once. A claim's files are in {SharedStorage.HostPath}/<namespace>/<name> on the engine, the same for every cluster made with shared storage, and stay when the claim is deleted."
        : "Made without shared storage, so only the node-local \"standard\" class. Shared storage can only be chosen when a cluster is created.";
    public string? SharedFolder => HasSharedStorage && Session.ContextName == "linux" ? SharedStorage.WindowsFolder(OperatingSystem.IsWindows()) : null;
    public bool HasSharedFolder => SharedFolder is not null;

    protected override object Key(KindCluster row) => row.Name;

    protected override void OnSelectionChanged(KindCluster? before)
    {
        Raise(nameof(KubectlHint), nameof(HasSharedStorage), nameof(SharedStorageText), nameof(SharedFolder), nameof(HasSharedFolder));
        RefreshCommands();
        ShowLoadBalancers();
    }

    public override void OnShown()
    {
        kind = KindCli.Locate();
        Raise(nameof(HasKind), nameof(Hint), nameof(HasHint));
        RefreshCommands();
    }

    public override Task Reload()
    {
        kind = KindCli.Locate();
        all = NeedsLinuxEngine ? [] : KindClusters.From(Session.Containers.Items);
        ApplyFilter();
        ShowLoadBalancers();
        Raise(nameof(HasKind), nameof(Hint), nameof(HasHint));
        RefreshCommands();
        return SyncController();
    }

    protected override IEnumerable<KindCluster> All => all;
    protected override string?[] Words(KindCluster row) => [row.Name, row.Context, row.KubernetesVersion];
    protected override SearchResult Describe(KindCluster row) => new(row.Name, row.Context, row);

    void ShowLoadBalancers()
    {
        var rows = Selected is { } c ? LoadBalancers.For(c.Name, Session.Containers.Items) : [];
        if (rows.SequenceEqual(LoadBalancerRows, LoadBalancerComparer.Instance)) return;
        LoadBalancerRows.Clear();
        foreach (var row in rows) LoadBalancerRows.Add(row);
        Raise(nameof(HasLoadBalancers));
    }

    // Only when what is wanted changes: every container event reloads this section.
    async Task SyncController()
    {
        if (NeedsLinuxEngine || !Session.IsConnected || Session.Client is not { } client || syncing) return;
        var wanted = HasKind && LoadBalancersEnabled && all.Count > 0;
        if (controllerWanted == wanted) return;
        syncing = true;
        try
        {
            await LoadBalancers.Sync(client, Session.ContextName.Length > 0 ? Session.ContextName : "default", wanted);
            controllerWanted = wanted;
        }
        catch (Exception ex) { Shell.ShowError($"Load balancers for kind: {ex.Message}"); }
        finally { syncing = false; }
    }

    void RefreshCommands()
    {
        CreateCommand.Refresh();
        DeleteCommand.Refresh();
        CopyKubeconfigCommand.Refresh();
        UseContextCommand.Refresh();
        DuplicateCommand.Refresh();
        RecreateCommand.Refresh();
        ToggleCommand.Refresh();
    }

    // The nodes are containers, so a stopped cluster keeps everything in it. Its load balancer proxies stop and start with it.
    public Task StartOrStop(KindCluster cluster) => Toggle(cluster);

    Task Toggle(KindCluster cluster) => Run(cluster.IsRunning ? $"Stopping {cluster.Name}" : $"Starting {cluster.Name}", async client =>
    {
        var proxies = Session.Containers.Items.Where(i => i.Labels.GetValueOrDefault(LoadBalancers.ClusterLabel) == cluster.Name).Select(i => i.Id).ToList();
        if (cluster.IsRunning)
        {
            await Task.WhenAll(proxies.Concat(cluster.Nodes.Select(n => n.Name)).Select(id => client.StopContainerAsync(id, timeoutSeconds: 30)));
            Shell.ShowInfo($"{cluster.Name} is stopped; Start brings it back as it was");
            return;
        }
        foreach (var node in cluster.StartOrder.Where(n => !n.IsRunning)) await client.StartContainerAsync(node.Name);
        await Task.WhenAll(proxies.Select(id => client.StartContainerAsync(id)));
        Shell.ShowInfo($"{cluster.Name} is starting; Kubernetes takes a minute to be ready");
    });

    public static int Workers(string choice) => int.TryParse(choice, out var n) ? n : 0;

    // The form writes the definition until it is changed by hand; a duplicate starts from the other cluster's.
    void AskCreate(ClusterDefinition? from = null, string? fromName = null)
    {
        var name = new DialogField("Name", placeholder: "dev");
        var workers = new DialogField("Worker nodes", choices: WorkerChoices);
        var helm = CliLocator.Locate("helm");
        var metrics = DialogField.Check($"metrics-server {ClusterAddons.MetricsServer.Version}: kubectl top and pod autoscaling", isChecked: from?.Addons.Contains(ClusterAddons.MetricsServer.Release) ?? true);
        var traefik = DialogField.Check($"Traefik {ClusterAddons.Traefik.Version}: an ingress controller, reachable through its load balancer", isChecked: from?.Addons.Contains(ClusterAddons.Traefik.Release) ?? false);
        string? generated = from is null ? KindCli.Config(0) : null;
        var definition = DialogField.Code("Definition: kind's cluster config", from?.Yaml ?? generated!, Shared.CodeLanguage.Yaml, ClusterDefinitions.Problem);
        var message = (fromName is null ? "Each node is a container on the Linux engine. The first cluster pulls kind's node image, which takes a few minutes. " : $"From {fromName}'s definition. ")
            + "Its context goes into your kubeconfig and becomes the current one."
            + (helm is null ? " Add Helm in Dockyard's window to install metrics-server and Traefik with it." : "");
        List<DialogField> fields = [name, .. from is null ? [workers] : Array.Empty<DialogField>(), .. helm is null ? Array.Empty<DialogField>() : [metrics, traefik], definition];
        Dialog? dialog = null;
        dialog = new Dialog(fromName is null ? "Create a kind cluster" : $"Duplicate {fromName}", message, "Create",
            d => Create(d, name.Value, definition.Text, d.Option, Addons(metrics, traefik, helm), helm),
            fields: fields,
            optionText: "Shared storage: a storage class every node mounts at once (ReadWriteMany), with one folder for all clusters made with it. It can only be added now.",
            canConfirm: _ => KindClusters.IsValidName(name.Value) && all.All(c => c.Name != name.Value) && definition.Problem is null);
        void Regenerate()
        {
            if (generated is null || definition.Text != generated) return;
            generated = KindCli.Config(Workers(workers.Value), dialog.Option);
            definition.Text = generated;
        }
        workers.Changed += Regenerate;
        dialog.Option = from?.SharedStorage ?? false;
        dialog.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Dialog.Option)) Regenerate(); };
        Shell.Open(dialog);
    }

    static List<ClusterAddon> Addons(DialogField metrics, DialogField traefik, string? helm) => helm is null ? [] :
        [.. metrics.IsChecked ? [ClusterAddons.MetricsServer] : Array.Empty<ClusterAddon>(), .. traefik.IsChecked ? [ClusterAddons.Traefik] : Array.Empty<ClusterAddon>()];

    Task Create(Dialog dialog, string name, string definition, bool sharedStorage, IReadOnlyList<ClusterAddon> addons, string? helm) => RunKind(dialog, async (actions, onLine) =>
    {
        ClusterDefinitions.Save(name, new ClusterDefinition(definition, sharedStorage, addons.Select(a => a.Release).ToList()));
        var file = await actions.Create(name, onLine, sharedStorage: sharedStorage, addons: addons, helm: helm, definition: definition);
        Shell.ShowInfo($"kubectl now uses {KindKubeconfig.Key(name)}, from {file}");
    });

    static ClusterDefinition DefinitionOf(KindCluster cluster) => ClusterDefinitions.Load(cluster.Name) ?? ClusterDefinitions.Guess(cluster);

    void AskDuplicate(KindCluster cluster) => AskCreate(DefinitionOf(cluster), cluster.Name);

    // kind cannot change a cluster: a changed definition means making it again, with everything in it gone.
    void AskRecreate(KindCluster cluster)
    {
        var saved = ClusterDefinitions.Load(cluster.Name);
        var current = saved ?? ClusterDefinitions.Guess(cluster);
        var definition = DialogField.Code("Definition: kind's cluster config", current.Yaml, Shared.CodeLanguage.Yaml, ClusterDefinitions.Problem);
        var addons = current.AddonList;
        var message = (saved is null ? "Dockyard did not make this cluster, so this is put together from its nodes. " : "")
            + "kind cannot change a cluster that exists: Recreate deletes it, with everything in it, and makes it again from this definition"
            + (addons.Count > 0 ? $", with {string.Join(" and ", addons.Select(a => a.Title))}." : ".");
        Shell.Open(new Dialog($"{cluster.Name}: definition", message, "Recreate", d => RunKind(d, async (actions, onLine) =>
        {
            var helm = CliLocator.Locate("helm");
            d.Report($"Deleting {cluster.Name}");
            await actions.Delete(cluster.Name, onLine);
            await RemoveProxies(cluster.Name);
            var remade = current with { Yaml = definition.Text, Addons = helm is null ? [] : current.Addons };
            ClusterDefinitions.Save(cluster.Name, remade);
            await actions.Create(cluster.Name, onLine, sharedStorage: remade.SharedStorage, addons: remade.AddonList, helm: helm, definition: remade.Yaml);
            Shell.ShowInfo($"{cluster.Name} is made again from its definition");
        }), danger: true, fields: [definition], canConfirm: _ => definition.Problem is null));
    }

    void AskDelete(IReadOnlyList<KindCluster> clusters)
    {
        if (clusters.Count == 0) return;
        var title = clusters.Count == 1 ? $"Delete cluster {clusters[0].Name}?" : $"Delete {clusters.Count} clusters?";
        var message = clusters.Count == 1 ? "Its nodes, everything in it and its context in the kubeconfig go." : "Their nodes, everything in them and their contexts in the kubeconfig go.";
        Ask(title, message, "Delete", d => RunKind(d, (actions, onLine) =>
            d.ForEach(clusters, "Deleting", c => c.Name, async c =>
            {
                await actions.Delete(c.Name, onLine);
                await RemoveProxies(c.Name);
                ClusterDefinitions.Remove(c.Name);
            })));
    }

    // The cloud provider stops looking after a cluster that is gone, so its proxies would stay behind.
    async Task RemoveProxies(string cluster)
    {
        if (Session.Client is not { } client) return;
        foreach (var proxy in Session.Containers.Items.Where(i => i.Labels.GetValueOrDefault(LoadBalancers.ClusterLabel) == cluster).ToList())
            await client.RemoveContainerAsync(proxy.Id, force: true);
    }

    async Task RunKind(Dialog dialog, Func<KindActions, Action<string>, Task> action)
    {
        if (kind is not { } exe) throw new InvalidOperationException("kind is not installed.");
        try
        {
            // The dialog shows the step at hand and the footer's activity keeps every line, also once the dialog is hidden.
            await action(new KindActions(exe, Session.ContextName, kubeconfig), line => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (line.Trim().Length == 0) return;
                if (!dialog.IsClosed) dialog.Report(line.Trim());
                else Shell.Activity.Note(line);
            }));
        }
        finally { await Reload(); }
    }

    async Task CopyKubeconfig()
    {
        if (Selected is not { } c || kind is not { } exe || CopyText is not { } copy) return;
        try
        {
            await copy(await new KindActions(exe, Session.ContextName, kubeconfig).Kubeconfig(c.Name));
            Shell.ShowInfo($"The kubeconfig of {c.Name} is on the clipboard");
        }
        catch (Exception ex) { Shell.ShowError(ex.Message); }
    }

    Task UseContext()
    {
        if (Selected is not { } c || kind is not { } exe) return Task.CompletedTask;
        return Run($"Making {c.Context} the current context", async () =>
        {
            var file = await new KindActions(exe, Session.ContextName, kubeconfig).UseContext(c.Name);
            Shell.ShowInfo($"kubectl now uses {c.Context}, from {file}");
        });
    }

    sealed class LoadBalancerComparer : IEqualityComparer<LoadBalancer>
    {
        public static readonly LoadBalancerComparer Instance = new();
        public bool Equals(LoadBalancer? x, LoadBalancer? y) => x is not null && y is not null && x.Service == y.Service && x.Address == y.Address && x.Ports.SequenceEqual(y.Ports);
        public int GetHashCode(LoadBalancer obj) => obj.Service.GetHashCode();
    }
}
