using System.Text.Json;
using Dockyard.Engine;

namespace Dockyard.Agent;

// The engine as the cluster services need it: the containers, and running, starting and removing Dockyard's own.
interface IClusterEngine
{
    Task<IReadOnlyList<ContainerSummary>> List(CancellationToken ct);
    // Pulls the image when it is not there; a container that was made but would not start is removed again.
    Task Run(ServiceContainerSpec spec, CancellationToken ct);
    Task Start(string id, CancellationToken ct);
    Task Remove(string id, CancellationToken ct);
    Task Connect(string network, string id, CancellationToken ct);
}

// kind's cloud provider, each running cluster's forwarder and dockyard-nfs, kept as the host wants them on every container
// event and on start, whether or not a UI is open. The wants are kept in a file, so a restarted agent has them too.
sealed class ClusterServices(IClusterEngine engine, string wantsFile, Action<string> log)
{
    const string NodeLabel = "io.x-k8s.kind.cluster";
    readonly object gate = new();
    List<ClusterWant>? wants = Load(wantsFile);
    List<ClusterStatus> status = [];
    // A port another program holds on the host shows only when a forwarder fails to start; it is tried again once the
    // cluster's own containers or its wants change, not in a loop.
    readonly Dictionary<string, (string Fingerprint, HashSet<int> Ports)> busy = [];
    readonly SemaphoreSlim changes = new(0, 1);

    static List<ClusterWant>? Load(string file)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), AgentApiJson.Default.ListClusterWant); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public void SetWants(IReadOnlyList<ClusterWant> next)
    {
        lock (gate) wants = [.. next];
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wantsFile)!);
            File.WriteAllText(wantsFile + ".new", JsonSerializer.Serialize(next.ToList(), AgentApiJson.Default.ListClusterWant));
            File.Move(wantsFile + ".new", wantsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"cannot keep the clusters' wants: {e.Message}"); }
        ContainersChanged();
    }

    public List<ClusterStatus> Status()
    {
        lock (gate) return status;
    }

    public void ContainersChanged()
    {
        if (changes.CurrentCount == 0) try { changes.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Reconcile(ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log($"cluster services: {e.Message}"); }
            await changes.WaitAsync(ct);
        }
    }

    public async Task Reconcile(CancellationToken ct)
    {
        List<ClusterWant>? wanted;
        lock (gate) wanted = wants;
        var containers = await engine.List(ct);
        var clusters = containers.Where(c => c.Labels?.ContainsKey(NodeLabel) == true)
            .GroupBy(c => c.Labels![NodeLabel])
            .ToDictionary(g => g.Key, g => g.All(n => n.State == "running"));
        // A cluster made elsewhere has a load balancer, as kind's cloud provider gives every cluster, and no fixed ports.
        ClusterWant WantOf(string cluster) => wanted?.FirstOrDefault(w => w.Cluster == cluster) ?? new ClusterWant(cluster, true, [], false);
        var running = clusters.Where(c => c.Value).Select(c => WantOf(c.Key)).ToList();

        await Keep(KindCloudProvider.Spec, running.Any(w => w.LoadBalancer), containers, ct);

        var nfs = running.Any(w => w.Nfs);
        // Only once the host said what it wants: dockyard-nfs holds no files itself, but a cluster may still be on its way.
        await Keep(KindNfsServer.Spec, nfs ? true : wanted is not null && !wanted.Any(w => w.Nfs) ? false : null, containers, ct);

        var claimed = new Dictionary<int, string>();
        var next = new List<ClusterStatus>();
        foreach (var (cluster, isRunning) in clusters.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var want = WantOf(cluster);
            var ports = isRunning && want.LoadBalancer ? want.Ports : [];
            var balancers = Balancers(cluster, containers);
            var (forwards, taken) = KindForwarder.Plan(ports, balancers, claimed, cluster);
            foreach (var forward in forwards) claimed.TryAdd(forward.Port, cluster);
            var fingerprint = Fingerprint(cluster, want, containers);
            if (!busy.TryGetValue(cluster, out var known) || known.Fingerprint != fingerprint) busy[cluster] = known = (fingerprint, []);
            var live = await KeepForwarder(cluster, forwards.Where(f => !known.Ports.Contains(f.Port)).ToList(), known.Ports, containers, ct);
            next.Add(new ClusterStatus(cluster, live.Select(f => new ClusterForward(f.Port, f.Service)).ToList(), taken, known.Ports.Order().ToList()));
        }
        foreach (var gone in busy.Keys.Where(k => !clusters.ContainsKey(k)).ToList()) busy.Remove(gone);
        // A forwarder whose cluster was deleted has nothing to forward to.
        foreach (var orphan in containers.Where(c => c.Labels?.GetValueOrDefault(KindForwarder.ClusterLabel) is { } cluster && !clusters.ContainsKey(cluster)))
        {
            await engine.Remove(orphan.Id, ct);
            log($"{orphan.Name} removed: its cluster is gone");
        }
        lock (gate) status = next;
    }

    static List<ClusterBalancer> Balancers(string cluster, IEnumerable<ContainerSummary> containers) => containers
        .Where(c => c.Labels?.GetValueOrDefault(KindCloudProvider.ClusterLabel) == cluster)
        .Select(c => new ClusterBalancer(
            c.Labels!.GetValueOrDefault(KindCloudProvider.NameLabel) is { } name && name.StartsWith(cluster + "/", StringComparison.Ordinal) ? name[(cluster.Length + 1)..] : c.Name,
            c.Name,
            c.Ports.Where(p => p.Type == "tcp" && p.PrivatePort != KindCloudProvider.EnvoyAdminPort).Select(p => p.PrivatePort).Distinct().ToList()))
        .ToList();

    static string Fingerprint(string cluster, ClusterWant want, IEnumerable<ContainerSummary> containers) =>
        $"{want.LoadBalancer}:{string.Join(',', want.Ports)};" + string.Join(';', containers
            .Where(c => c.Labels?.GetValueOrDefault(NodeLabel) == cluster || c.Labels?.GetValueOrDefault(KindCloudProvider.ClusterLabel) == cluster)
            .Select(c => $"{c.Id}:{c.State}").Order(StringComparer.Ordinal));

    // Returns what forwards: a port another program holds is left out, and added to busy.
    async Task<List<PlannedForward>> KeepForwarder(string cluster, List<PlannedForward> forwards, HashSet<int> busyPorts, IReadOnlyList<ContainerSummary> containers, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Keep(KindForwarder.Spec(cluster, forwards), forwards.Count > 0, containers, ct);
                return forwards;
            }
            catch (DockerException e) when (KindForwarder.BusyPort(e.Message) is { } port && forwards.Any(f => f.Port == port))
            {
                log($"port {port} for cluster {cluster} is in use by another program");
                busyPorts.Add(port);
                forwards.RemoveAll(f => f.Port == port);
                containers = await engine.List(ct);
            }
        }
    }

    // wanted null leaves it as it is.
    async Task Keep(ServiceContainerSpec spec, bool? wanted, IReadOnlyList<ContainerSummary> containers, CancellationToken ct)
    {
        var current = containers.FirstOrDefault(c => c.Names.Contains("/" + spec.Name));
        if (wanted is null) return;
        if (wanted == false)
        {
            if (current is null) return;
            await engine.Remove(current.Id, ct);
            log($"{spec.Name} removed: no running cluster needs it");
            return;
        }
        if (current is not null && (spec.SetupLabel is null || current.Labels?.GetValueOrDefault(spec.SetupLabel) == spec.Setup))
        {
            if (current.NetworkSettings?.Networks?.ContainsKey(ServiceContainerSpec.Network) == false) await engine.Connect(ServiceContainerSpec.Network, current.Id, ct);
            if (current.State != "running") await engine.Start(current.Id, ct);
            return;
        }
        if (current is not null) await engine.Remove(current.Id, ct);
        await engine.Run(spec, ct);
        log($"{spec.Name} runs");
    }
}

sealed class DockerClusterEngine(Func<DockerClient> connect, Action<string> log) : IClusterEngine
{
    public async Task<IReadOnlyList<ContainerSummary>> List(CancellationToken ct)
    {
        using var docker = connect();
        return await docker.ListContainersAsync(all: true, ct);
    }

    public async Task Run(ServiceContainerSpec spec, CancellationToken ct)
    {
        using var docker = connect();
        await Services.EnsureImage(docker, spec.Image, log, ct);
        var id = (await docker.PostNodeAsync($"containers/create?name={Uri.EscapeDataString(spec.Name)}", spec.CreateBody(), ct))!["Id"]!.GetValue<string>();
        try { await docker.StartContainerAsync(id, ct); }
        catch (DockerException)
        {
            await docker.RemoveContainerAsync(id, force: true, ct: ct);
            throw;
        }
    }

    public async Task Start(string id, CancellationToken ct)
    {
        using var docker = connect();
        await docker.StartContainerAsync(id, ct);
    }

    public async Task Remove(string id, CancellationToken ct)
    {
        using var docker = connect();
        await docker.RemoveContainerAsync(id, force: true, ct: ct);
    }

    public async Task Connect(string network, string id, CancellationToken ct)
    {
        using var docker = connect();
        await docker.ConnectNetworkAsync(network, id, ct);
    }
}
