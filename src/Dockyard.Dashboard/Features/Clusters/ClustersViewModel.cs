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
    readonly Func<Task> pushWants;

    public ClustersViewModel(ShellViewModel shell, EngineSession session, HostKubeconfig? kubeconfig = null, DashboardSettings? settings = null, Func<Task>? pushWants = null) : base(shell, session)
    {
        this.kubeconfig = kubeconfig ?? HostKubeconfig.FromEnvironment();
        this.settings = settings ?? DashboardSettings.Current;
        this.pushWants = pushWants ?? PushWants;
        CreateCommand = new Command(() => { AskCreate(); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine);
        DeleteCommand = new Command(() => { AskDelete(Picked); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine && SelectionCount > 0);
        DeleteRowCommand = new Command<KindCluster>(c => { AskDelete([c]); return Task.CompletedTask; });

        EditCommand = new Command(() => Selected is { } c ? AskEdit(c) : Task.CompletedTask, () => !IsBusy && HasKind && !NeedsLinuxEngine && Selected is { IsRunning: true });
        OpenHeadlampCommand = new Command(() =>
        {
            if (HeadlampUrl is { } url)
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) { Shell.ShowError($"Could not open {url}: {ex.Message}"); }
            return Task.CompletedTask;
        }, () => !NeedsLinuxEngine && Selected is { IsRunning: true } && HasHeadlamp);
        CopyHeadlampTokenCommand = new Command(CopyHeadlampToken, () => HasKind && !NeedsLinuxEngine && Selected is { IsRunning: true });
        CopyKubeconfigCommand = new Command(CopyKubeconfig, () => HasKind && !NeedsLinuxEngine && Selected is { IsRunning: true });
        UseContextCommand = new Command(UseContext, () => !IsBusy && HasKind && !NeedsLinuxEngine && Selected is { IsRunning: true });
        DuplicateCommand = new Command(() => { if (Selected is { } c) AskDuplicate(c); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine && Selected is not null);
        RecreateCommand = new Command(() => { if (Selected is { } c) AskRecreate(c); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine && Selected is not null);
        UpgradeCommand = new Command(() => { if (Selected is { } c) AskUpgrade(c); return Task.CompletedTask; }, () => !IsBusy && HasKind && !NeedsLinuxEngine && CanUpgrade);
        ToggleCommand = new Command(() => Selected is { } c ? Toggle(c) : Task.CompletedTask, () => !IsBusy && !NeedsLinuxEngine && Selected is not null);
        ToggleRowCommand = new Command<KindCluster>(c => IsBusy ? Task.CompletedTask : Toggle(c));
        LoadBalancerCommand = new Command(() => { if (Selected is { } c) AskLoadBalancer(c); return Task.CompletedTask; }, () => HasKind && !NeedsLinuxEngine && Selected is not null);
        OpenUrlCommand = new Command<PublishedPort>(p =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p.Url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Shell.ShowError($"Could not open {p.Url}: {ex.Message}"); }
            return Task.CompletedTask;
        });

        OpenSharedFolderCommand = new Command(() =>
        {
            if (SharedFolder is { } folder)
                try { Features.Files.FileManager.Open(folder); }
                catch (Exception ex) { Shell.ShowError($"Could not open {folder}: {ex.Message}"); }
            return Task.CompletedTask;
        });

        session.Connected += () => _ = Reload();
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
    public Command CopyHeadlampTokenCommand { get; }
    public Command EditCommand { get; }

    // Points the details at a cluster by name, for Containers, whose rows are its own copies.
    public bool Select(string name)
    {
        if (Rows.FirstOrDefault(r => r.Name == name) is not { } row) return false;
        Selected = row;
        return true;
    }

    public Command OpenHeadlampCommand { get; }

    // Headlamp's address for the selected cluster, as its definition has it: https with cert-manager's certificate.
    public string? HeadlampUrl => Selected is { } c && ClusterDefinitions.Load(c.Name) is { } d && d.Addons.Contains(Headlamp.Addon.Release)
        ? $"{(d.Addons.Contains(CertManager.Addon.Release) ? "https" : "http")}://{Headlamp.Host(c.Name)}" : null;
    public bool HasHeadlamp => HeadlampUrl is not null;
    public string DefinitionYaml => Selected is { } c ? DefinitionOf(c).Yaml : "";
    public Command DuplicateCommand { get; }
    public Command RecreateCommand { get; }
    public Command UpgradeCommand { get; }
    public bool CanUpgrade => Selected is { } c && KindImages.NewerThan(c.KubernetesVersion).Count > 0;
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

    static ClusterDefinition? DefinitionFor(string cluster) => ClusterDefinitions.Load(cluster);
    static bool WantsLoadBalancer(string cluster) => DefinitionFor(cluster)?.LoadBalancer ?? true;

    public bool SelectedLoadBalancer => Selected is { } c && WantsLoadBalancer(c.Name);

    public string LoadBalancerHint => Selected is not { } c ? ""
        : !WantsLoadBalancer(c.Name) ? "Off for this cluster: Dockyard publishes none of its ports. While another cluster's load balancer runs, kind's cloud provider still gives its LoadBalancer services an address."
        : "A service of type LoadBalancer gets an address here, and its ports on this computer.";

    public string ForwardsText => Selected is { } c && forwardNotes.TryGetValue(c.Name, out var note) ? note : "";
    public bool HasForwards => ForwardsText.Length > 0;

    Dictionary<string, string> forwardNotes = [];

    // What failed for every cluster, such as kind's cloud provider: said once, above the containers, with or without clusters.
    public string ServicesError { get => servicesError; private set { if (Set(ref servicesError, value)) Raise(nameof(HasServicesError)); } }
    public bool HasServicesError => servicesError.Length > 0;
    string servicesError = "";
    readonly Dictionary<string, string> loggedForwards = [];

    public Command LoadBalancerCommand { get; }

    public string Hint =>
        !HasKind ? "kind is not installed. Add it under Engines and settings, or run the setup again with --with kind."
        : NeedsLinuxEngine ? "kind runs on the Linux engine. Switch to Linux to see and create clusters."
        : Rows.Count == 0 ? "No clusters yet. kind runs each node as a container on this engine."
        : "";

    public bool HasHint => Hint.Length > 0;
    public string? HintLink => HasKind ? null : "Engines and settings";
    public string KubectlHint => Selected is { } c ? $"kubectl --context {c.Context} get nodes" : "";

    public bool HasSharedStorage => Selected?.HasSharedStorage == true;
    public string SharedStorageText => HasSharedStorage
        ? $"Storage class \"{SharedStorage.ClassName}\": ReadWriteMany, so pods on every node use a claim at once. A claim's files are in {SharedStorage.HostPath}/<namespace>/<name> on the engine, the same for every cluster made with shared storage, and stay when the claim is deleted."
        : "Made without shared storage, so only the node-local \"standard\" class. Shared storage can only be chosen when a cluster is created.";
    // On Windows through WSL's share, on a Linux host where it is; on macOS it is inside the VM, with nothing to open.
    public string? SharedFolder => !HasSharedStorage ? null
        : OperatingSystem.IsWindows() ? (Session.ContextName == "linux" ? SharedStorage.WindowsFolder(true) : null)
        : OperatingSystem.IsLinux() ? SharedStorage.HostPath
        : null;
    public bool HasSharedFolder => SharedFolder is not null;

    protected override object Key(KindCluster row) => row.Name;

    protected override void OnSelectionChanged(KindCluster? before)
    {
        Raise(nameof(KubectlHint), nameof(HasSharedStorage), nameof(SharedStorageText), nameof(SharedFolder), nameof(HasSharedFolder), nameof(SelectedLoadBalancer), nameof(LoadBalancerHint), nameof(ForwardsText), nameof(HasForwards));
        RefreshCommands();
        ShowLoadBalancers();
    }

    public override void OnShown()
    {
        kind = KindCli.Locate();
        Raise(nameof(HasKind), nameof(Hint), nameof(HasHint), nameof(HintLink));
        RefreshCommands();
    }

    public override Task Reload()
    {
        kind = KindCli.Locate();
        all = NeedsLinuxEngine ? [] : KindClusters.From(Session.Containers.Items);
        ApplyFilter();
        ShowLoadBalancers();
        Raise(nameof(HasKind), nameof(Hint), nameof(HasHint), nameof(HintLink));
        RefreshCommands();
        return RefreshForwards();
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

    // The engine's agent keeps the cloud provider, the forwarders and dockyard-nfs running; this shows what it made of
    // each cluster's ports.
    async Task RefreshForwards()
    {
        if (NeedsLinuxEngine || !Session.IsConnected || Session.Client is not { } client || Dockyard.Engine.AgentClient.For(client.Endpoint) is not { } agent) return;
        Dockyard.Engine.ClusterServicesStatus report;
        try { report = await agent.ClusterStatusAsync(); }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException or TimeoutException) { return; }
        var errors = LoadBalancers.ServiceErrors(report);
        if (errors != ServicesError && errors.Length > 0) Shell.Activity.Note(errors, ActivityKind.Error);
        ServicesError = errors;
        foreach (var status in report.Clusters)
        {
            var note = LoadBalancers.Activity(status);
            if (note != loggedForwards.GetValueOrDefault(status.Cluster, "") && (note.Length > 0 || loggedForwards.ContainsKey(status.Cluster)))
                Shell.Activity.Note($"{status.Cluster} load balancer: {(note.Length > 0 ? note : "no ports")}", ActivityKind.Info);
            loggedForwards[status.Cluster] = note;
        }
        forwardNotes = report.Clusters.ToDictionary(st => st.Cluster, st => LoadBalancers.Notes(st));
        Raise(nameof(ForwardsText), nameof(HasForwards));
    }

    internal Task PushWants() => Shell.SetupSource.PushClusterWants();

    void AskLoadBalancer(KindCluster cluster)
    {
        var current = DefinitionFor(cluster.Name) ?? ClusterDefinitions.Guess(cluster);
        var on = DialogField.Check("A load balancer for this cluster", current.LoadBalancer);
        var ports = PortsField(current.Ports ?? ClusterDefinition.DefaultPorts, cluster.Name);
        ports.IsShown = on.IsChecked;
        on.Changed += () => ports.IsShown = on.IsChecked;
        Shell.Open(new Dialog($"{cluster.Name}: load balancer",
            $"LoadBalancer services get an address from kind's cloud provider, and an ingress controller's hosts are reachable by name as <host>.{cluster.Name}.dockyard.internal on 80 and 443. Fixed ports go to the service that has them on this computer's 127.0.0.1; its other ports keep the one Docker picks.",
            "Save", _ =>
            {
                ClusterDefinitions.Save(cluster.Name, current with { LoadBalancer = on.IsChecked, Ports = ClusterDefinitions.ParsePorts(ports.Text) ?? current.Ports });
                Raise(nameof(SelectedLoadBalancer), nameof(LoadBalancerHint));
                return pushWants();
            },
            fields: [on, ports], canConfirm: _ => !on.IsChecked || ports.Problem is null));
    }

    DialogField PortsField(IReadOnlyList<int> ports, string? cluster) => new("Fixed ports on 127.0.0.1, if any", string.Join(", ", ports), "none, or such as 8080, 8443")
    {
        Validate = text => ClusterDefinitions.ParsePorts(text) is null ? "A list of ports, such as 8080, 8443, or nothing" : null,
        Warn = text => ClusterDefinitions.ParsePorts(text) is not { } list ? null
            : all.Where(c => c.Name != cluster).Select(c => (c.Name, Ports: DefinitionFor(c.Name)?.LoadBalancerPorts ?? []))
                .SelectMany(o => o.Ports.Intersect(list).Select(p => $"Port {p} is also cluster {o.Name}'s: while both have a service on it, the first by name gets it and the other keeps the port Docker picks"))
                .FirstOrDefault(),
    };

    void RefreshCommands()
    {
        CreateCommand.Refresh();
        DeleteCommand.Refresh();
        CopyKubeconfigCommand.Refresh();
        UseContextCommand.Refresh();
        EditCommand.Refresh();
        CopyHeadlampTokenCommand.Refresh();
        OpenHeadlampCommand.Refresh();
        Raise(nameof(HeadlampUrl), nameof(HasHeadlamp), nameof(DefinitionYaml));
        DuplicateCommand.Refresh();
        RecreateCommand.Refresh();
        UpgradeCommand.Refresh();
        Raise(nameof(CanUpgrade));
        LoadBalancerCommand.Refresh();
        ToggleCommand.Refresh();
    }

    // The nodes are containers, so a stopped cluster keeps everything in it. Its load balancer proxies stop and start with it.
    public Task StartOrStop(KindCluster cluster) => Toggle(cluster);

    Task Toggle(KindCluster cluster) => cluster.IsRunning ? Stop(cluster) : Start(cluster);

    List<string> ProxiesOf(KindCluster cluster) =>
        Session.Containers.Items.Where(i => OwnedBy(i, cluster.Name)).Select(i => i.Id).ToList();

    static bool OwnedBy(ContainerItem item, string cluster) =>
        item.Labels.GetValueOrDefault(LoadBalancers.ClusterLabel) == cluster || item.Labels.GetValueOrDefault(LoadBalancers.ForwarderClusterLabel) == cluster;

    public Task Stop(KindCluster cluster) => Run($"Stopping {cluster.Name}", async client =>
    {
        await Task.WhenAll(ProxiesOf(cluster).Concat(cluster.Nodes.Where(n => n.IsRunning).Select(n => n.Name)).Select(id => client.StopContainerAsync(id, timeoutSeconds: 30)));
        Shell.ShowInfo($"{cluster.Name} is stopped; Start brings it back as it was");
    });

    public Task Start(KindCluster cluster) => Run($"Starting {cluster.Name}", async client =>
    {
        foreach (var node in cluster.StartOrder.Where(n => !n.IsRunning)) await client.StartContainerAsync(node.Name);
        await Task.WhenAll(ProxiesOf(cluster).Select(id => client.StartContainerAsync(id)));
        Shell.ShowInfo($"{cluster.Name} is starting; Kubernetes takes a minute to be ready");
    });

    public static int Workers(string choice) => int.TryParse(choice, out var n) ? n : 0;

    // The form writes the definition until it is changed by hand; a duplicate starts from the other cluster's.
    static readonly string[] StorageChoices =
    [
        "Node-local only (standard)",
        "Shared host folder (class \"shared\", ReadWriteMany)",
        "NFS (class \"nfs\", served by the dockyard-nfs container)",
    ];

    static ClusterStorage StorageOf(string choice) => Array.IndexOf(StorageChoices, choice) switch
    {
        1 => ClusterStorage.HostFolder,
        2 => ClusterStorage.Nfs,
        _ => ClusterStorage.None,
    };

    // What to put in comes first, kind's config that follows from it last: the form writes it until it is changed by
    // hand. A duplicate starts from the other cluster's.
    void AskCreate(ClusterDefinition? from = null, string? fromName = null)
    {
        var helm = CliLocator.Locate("helm");
        var name = new DialogField("Name", placeholder: "dev");
        var workers = new DialogField("Worker nodes", choices: WorkerChoices);
        var version = new DialogField("Kubernetes version", choices: KindImages.All.Select(i => i.Version).ToList())
        {
            Choice = KindImages.For(from?.KubernetesVersion)?.Version ?? KindImages.Default.Version,
        };
        var storage = new DialogField("Storage that every node can use at once", choices: helm is null ? StorageChoices[..2] : StorageChoices)
        {
            Choice = StorageChoices[from?.Storage switch { ClusterStorage.HostFolder => 1, ClusterStorage.Nfs when helm is not null => 2, _ => 0 }],
        };
        var balancer = DialogField.Check("Load balancer: LoadBalancer services get an address, an ingress controller's hosts are reachable as <host>.<name>.dockyard.internal, and external-dns publishes Ingress hosts under dockyard.internal", from?.LoadBalancer ?? true);
        var ports = PortsField(from?.Ports ?? ClusterDefinition.DefaultPorts, null);
        ports.IsShown = balancer.IsChecked;
        balancer.Changed += () => ports.IsShown = balancer.IsChecked;
        var metrics = DialogField.Check($"metrics-server {ClusterAddons.MetricsServer.Version}: kubectl top and pod autoscaling", isChecked: from?.Addons.Contains(ClusterAddons.MetricsServer.Release) ?? true);
        var traefik = DialogField.Check($"Traefik {ClusterAddons.Traefik.Version}: an ingress controller, reachable through the load balancer", isChecked: from?.Addons.Contains(ClusterAddons.Traefik.Release) ?? false);
        var certManager = DialogField.Check($"cert-manager {CertManager.Addon.Version}: certificates for <host>.<name>.dockyard.internal from the ClusterIssuer dockyard, trusted on this computer when trusted HTTPS is on", isChecked: from?.Addons.Contains(CertManager.Addon.Release) ?? false);
        var victoria = DialogField.Check($"VictoriaMetrics {VictoriaMetrics.Addon.Version}: metrics history for Headlamp's charts, light on memory", isChecked: from?.Addons.Contains(VictoriaMetrics.Addon.Release) ?? false);
        var headlamp = DialogField.Check($"Headlamp {Headlamp.Addon.Version}: the Kubernetes UI at dashboard.<name>.dockyard.internal, with Traefik", isChecked: from?.Addons.Contains(Headlamp.Addon.Release) ?? false);
        headlamp.Changed += () => { if (headlamp.IsChecked) traefik.IsChecked = true; };
        string? generated = from is null ? KindCli.Config(0) : null;
        var definition = DialogField.Code("Definition: kind's cluster config, from the choices above until you change it", from?.Yaml ?? generated!, Shared.CodeLanguage.Yaml, ClusterDefinitions.Problem);
        var message = (fromName is null ? "Each node is a container on the Linux engine. The first cluster pulls kind's node image, which takes a few minutes. " : $"From {fromName}'s definition. ")
            + "Its context goes into your kubeconfig and becomes the current one."
            + (helm is null ? " Add Helm in Dockyard's window for metrics-server, Traefik and NFS storage." : "");
        List<DialogField> fields =
        [
            name, version, .. from is null ? [workers] : Array.Empty<DialogField>(), storage, balancer, ports,
            .. helm is null ? Array.Empty<DialogField>() : [metrics, traefik, certManager, victoria, headlamp], definition,
        ];
        void Regenerate()
        {
            if (generated is null || definition.Text != generated) return;
            generated = KindCli.Config(Workers(workers.Value), StorageOf(storage.Value) == ClusterStorage.HostFolder);
            definition.Text = generated;
        }
        workers.Changed += Regenerate;
        storage.Changed += Regenerate;
        Shell.Open(new Dialog(fromName is null ? "Create a kind cluster" : $"Duplicate {fromName}", message, "Create",
            d => Create(d, name.Value, new ClusterDefinition(definition.Text, StorageOf(storage.Value), Addons(metrics, traefik, certManager, victoria, headlamp, helm).Select(a => a.Release).ToList(),
                balancer.IsChecked, ClusterDefinitions.ParsePorts(ports.Text) ?? ClusterDefinition.DefaultPorts, version.Value), helm),
            fields: fields,
            canConfirm: _ => KindClusters.IsValidName(name.Value) && all.All(c => c.Name != name.Value) && definition.Problem is null && (!balancer.IsChecked || ports.Problem is null)));
    }

    static List<ClusterAddon> Addons(DialogField metrics, DialogField traefik, DialogField certManager, DialogField victoria, DialogField headlamp, string? helm) => helm is null ? [] :
        [
            .. metrics.IsChecked ? [ClusterAddons.MetricsServer] : Array.Empty<ClusterAddon>(), .. traefik.IsChecked ? [ClusterAddons.Traefik] : Array.Empty<ClusterAddon>(),
            .. certManager.IsChecked ? [CertManager.Addon] : Array.Empty<ClusterAddon>(),
            .. victoria.IsChecked ? [VictoriaMetrics.Addon] : Array.Empty<ClusterAddon>(),
            .. headlamp.IsChecked ? [Headlamp.Addon] : Array.Empty<ClusterAddon>(),
        ];

    Task Create(Dialog dialog, string name, ClusterDefinition definition, string? helm) => RunKind(dialog, async (actions, onLine) =>
    {
        ClusterDefinitions.Save(name, definition);
        await pushWants();
        var file = await actions.Create(name, onLine, storage: definition.Storage, addons: definition.AddonList, helm: helm, definition: definition.Yaml, publishNames: definition.LoadBalancer,
            clusterCa: ClusterCa, image: KindImages.For(definition.KubernetesVersion));
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
            await Remake(d, actions, onLine, cluster, current with { Yaml = definition.Text, Addons = helm is null ? [] : current.Addons }, helm);
            Shell.ShowInfo($"{cluster.Name} is made again from its definition");
        }), danger: true, fields: [definition], canConfirm: _ => definition.Problem is null));
    }

    internal Task<string> ClusterCa(string cluster) => Shell.SetupSource.ClusterCa(cluster);

    // kind changes nothing in a cluster that exists: Recreate, a change to or from the host folder and Upgrade all delete
    // it, with everything in it, and make it again from the definition.
    async Task Remake(Dialog dialog, KindActions actions, Action<string> onLine, KindCluster cluster, ClusterDefinition remade, string? helm)
    {
        dialog.Report($"Deleting {cluster.Name}");
        await actions.Delete(cluster.Name, onLine);
        await RemoveProxies(cluster.Name);
        ClusterDefinitions.Save(cluster.Name, remade);
        await pushWants();
        await actions.Create(cluster.Name, onLine, storage: remade.Storage, addons: remade.AddonList, helm: helm, definition: remade.Yaml,
            publishNames: remade.LoadBalancer, clusterCa: ClusterCa, image: KindImages.For(remade.KubernetesVersion));
    }

    // No upgrade in place (kind has none): the workloads and node-local volumes go, files on shared and NFS storage stay.
    void AskUpgrade(KindCluster cluster)
    {
        var newer = KindImages.NewerThan(cluster.KubernetesVersion);
        if (newer.Count == 0) return;
        var version = new DialogField("Kubernetes version", choices: newer.Select(i => i.Version).ToList()) { Choice = newer[0].Version };
        var current = DefinitionOf(cluster);
        var addons = current.AddonList;
        var message = $"kind cannot upgrade a cluster in place. Upgrade deletes {cluster.Name} on Kubernetes {cluster.KubernetesVersion ?? "(unknown)"}, with every workload in it and the data on its node-local volumes, "
            + "and makes it again on the version chosen, from its definition"
            + (addons.Count > 0 ? $", with {string.Join(" and ", addons.Select(a => a.Title))}" : "")
            + ". Files on shared and NFS storage stay.";
        Shell.Open(new Dialog($"Upgrade {cluster.Name}", message, "Upgrade", d => RunKind(d, async (actions, onLine) =>
        {
            var helm = CliLocator.Locate("helm");
            await Remake(d, actions, onLine, cluster, current with { KubernetesVersion = version.Value, Addons = helm is null ? [] : current.Addons }, helm);
            Shell.ShowInfo($"{cluster.Name} runs Kubernetes {version.Value}");
        }), danger: true, fields: [version]));
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
                await pushWants();
            })));
    }

    // The cloud provider stops looking after a cluster that is gone, so its proxies would stay behind.
    async Task RemoveProxies(string cluster)
    {
        if (Session.Client is not { } client) return;
        foreach (var proxy in Session.Containers.Items.Where(i => OwnedBy(i, cluster)).ToList())
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

    // What can change on a running cluster: its add-ons, through Helm. Storage and the kind config are the nodes' own and
    // need Recreate.
    async Task AskEdit(KindCluster cluster)
    {
        if (CliLocator.Locate("helm") is not { } helm)
        {
            Shell.ShowError("The add-ons go in with Helm, which is not installed. Add it in Tools.");
            return;
        }
        var saved = DefinitionOf(cluster);
        if (kind is not { } exe) return;
        // As the cluster is now: a chart taken out with helm or kubectl, or NFS removed by hand, shows as gone.
        List<ClusterAddon> actual;
        bool actualNfs;
        try { (actual, actualNfs) = await new KindActions(exe, Session.ContextName, kubeconfig).Actual(cluster.Name, helm); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Text.Json.JsonException) { Shell.ShowError(ex.Message); return; }
        var current = saved with
        {
            Addons = actual.Select(a => a.Release).ToList(),
            Storage = saved.Storage == ClusterStorage.HostFolder ? ClusterStorage.HostFolder : actualNfs ? ClusterStorage.Nfs : ClusterStorage.None,
        };
        if (!current.Addons.SequenceEqual(saved.Addons) || current.Storage != saved.Storage)
        {
            ClusterDefinitions.Save(cluster.Name, current);
            await pushWants();
            RefreshCommands();
        }
        bool Has(ClusterAddon a) => current.Addons.Contains(a.Release);
        var metrics = DialogField.Check($"metrics-server {ClusterAddons.MetricsServer.Version}: kubectl top and pod autoscaling", Has(ClusterAddons.MetricsServer));
        var traefik = DialogField.Check($"Traefik {ClusterAddons.Traefik.Version}: an ingress controller, reachable through the load balancer", Has(ClusterAddons.Traefik));
        var certManager = DialogField.Check($"cert-manager {CertManager.Addon.Version}: certificates from the ClusterIssuer dockyard", Has(CertManager.Addon));
        var victoria = DialogField.Check($"VictoriaMetrics {VictoriaMetrics.Addon.Version}: metrics history for Headlamp's charts, light on memory", Has(VictoriaMetrics.Addon));
        var headlamp = DialogField.Check($"Headlamp {Headlamp.Addon.Version}: the Kubernetes UI at {Headlamp.Host(cluster.Name)}, with Traefik", Has(Headlamp.Addon));
        headlamp.Changed += () => { if (headlamp.IsChecked) traefik.IsChecked = true; };
        var storage = new DialogField("Storage that every node can use at once", choices: StorageChoices)
        {
            Choice = StorageChoices[current.Storage switch { ClusterStorage.HostFolder => 1, ClusterStorage.Nfs => 2, _ => 0 }],
        };
        // The host folder is mounted on each node when kind makes it: choosing it, or leaving it, makes the cluster anew.
        bool NeedsRecreate() => (StorageOf(storage.Value) == ClusterStorage.HostFolder) != (current.Storage == ClusterStorage.HostFolder);
        Dialog? dialog = null;
        dialog = new Dialog($"{cluster.Name}: add-ons and storage",
            "Add-ons and NFS change on the running cluster. A change to or from the shared host folder makes it Recreate: the cluster is deleted, with everything in it, and made again with these choices. Workers and the kind config change under Definition.", "Apply",
            d => RunKind(d, async (actions, onLine) =>
            {
                var wanted = Addons(metrics, traefik, certManager, victoria, headlamp, helm);
                var chosen = StorageOf(storage.Value);
                if (NeedsRecreate())
                {
                    await Remake(d, actions, onLine, cluster,
                        current with { Yaml = KindCli.WithSharedStorage(current.Yaml, chosen == ClusterStorage.HostFolder), Storage = chosen, Addons = wanted.Select(a => a.Release).ToList() }, helm);
                    Shell.ShowInfo($"{cluster.Name} is made again with the add-ons and storage as chosen");
                    return;
                }
                bool? nfs = chosen == current.Storage ? null : chosen == ClusterStorage.Nfs;
                await actions.EditAddons(cluster.Name, current.AddonList, wanted, helm, ClusterCa, onLine, nfs);
                ClusterDefinitions.Save(cluster.Name, current with { Addons = wanted.Select(a => a.Release).ToList(), Storage = chosen });
                await pushWants();
                Shell.ShowInfo($"{cluster.Name} has the add-ons and storage as chosen");
            }), fields: [metrics, traefik, certManager, victoria, headlamp, storage]);
        storage.Changed += () =>
        {
            var recreate = NeedsRecreate();
            dialog.ConfirmText = recreate ? "Recreate" : "Apply";
            dialog.IsDanger = recreate;
        };
        Shell.Open(dialog);
    }

    async Task CopyHeadlampToken()
    {
        if (Selected is not { } c) return;
        if (CopyText is not { } copy) { Shell.ShowError("Could not reach the clipboard."); return; }
        try
        {
            await copy(await Headlamp.Token(Session.ContextName.Length > 0 ? Session.ContextName : "default", c.Name));
            Shell.ShowInfo($"An admin token for {c.Name} is on the clipboard, valid for a day; sign in to Headlamp with it");
        }
        catch (Exception ex) { Shell.ShowError(ex.Message); }
    }

    async Task CopyKubeconfig()
    {
        if (Selected is not { } c || kind is not { } exe) return;
        if (CopyText is not { } copy) { Shell.ShowError("Could not reach the clipboard."); return; }
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
