using System.Net;
using System.Text.Json;
using Dockyard.Engine;

namespace Dockyard.Agent;

// Names to containers, and which port to use for each, kept up to date from the engine's events.
sealed class DomainTable(Func<DockerClient> connect, string choicesFile, Action<string> log)
{
    readonly object gate = new();
    Dictionary<string, DomainTarget> byDomain = [];
    Dictionary<string, ClusterIngress> clusters = [];
    const string ClusterLabel = "io.x-k8s.cloud-provider-kind.cluster";
    Dictionary<string, int> chosen = LoadChoices(choicesFile);

    static Dictionary<string, int> LoadChoices(string file)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(file), AgentApiJson.Default.DictionaryStringInt32) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    // Container domains switched off in the Toolbox: no names, no routes, no cluster ingress; kept in a file next to the
    // port choices, so a restarted agent stays off.
    string OffMarker => Path.Combine(Path.GetDirectoryName(choicesFile)!, "names-off");

    public bool Enabled
    {
        get => !File.Exists(OffMarker);
        set
        {
            try
            {
                if (value) File.Delete(OffMarker);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(OffMarker)!);
                    File.WriteAllText(OffMarker, "");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"cannot keep container domains {(value ? "on" : "off")}: {e.Message}"); }
            Changed();
        }
    }

    public DomainTarget? Find(string domain)
    {
        if (!Enabled) return null;
        lock (gate) return byDomain.GetValueOrDefault(domain);
    }

    public ClusterIngress? Ingress(string domain)
    {
        if (!Enabled) return null;
        lock (gate) return DomainNames.ClusterOf(domain, clusters.Keys) is { } cluster ? clusters[cluster] : null;
    }

    // A cluster can have several LoadBalancer services on 80 or 443; the ingress controller is the one with both, and
    // otherwise the choice is by name, so it does not change with the order the engine lists containers in.
    internal static ClusterIngress PickIngress(string cluster, IEnumerable<(string Cluster, string Name, IPAddress Address, bool Http, bool Https)> balancers)
    {
        var ordered = balancers.OrderByDescending(b => b.Http && b.Https).ThenBy(b => b.Name, StringComparer.Ordinal).ToList();
        return new ClusterIngress(cluster, ordered.FirstOrDefault(b => b.Http).Address, ordered.FirstOrDefault(b => b.Https).Address);
    }

    public bool Contains(string domain) => Find(domain) is not null || Ingress(domain) is not null;

    // Chosen by name, so it outlives the container: Compose replaces containers whenever their configuration changes.
    public int? Port(DomainTarget target)
    {
        int? choice;
        lock (gate) choice = chosen.TryGetValue(target.ContainerName, out var c) ? c : null;
        return DomainNames.PickPort(choice, target.Label, Listening(target), target.Exposed);
    }

    static IEnumerable<int> Listening(DomainTarget target)
    {
        if (target.Pid is not { } pid) return [];
        List<int> ports = [];
        foreach (var file in new[] { "tcp", "tcp6" })
        {
            try { ports.AddRange(ListeningPorts.Parse(File.ReadAllText($"/proc/{pid}/net/{file}"), udp: false)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return ports;
    }

    public List<DomainInfo> List()
    {
        List<DomainTarget> targets;
        lock (gate) targets = byDomain.Values.ToList();
        return targets.Select(t =>
        {
            bool isChosen;
            lock (gate) isChosen = chosen.ContainsKey(t.ContainerName);
            return new DomainInfo(t.ContainerId, t.Domain, Port(t), isChosen);
        }).OrderBy(d => d.Domain).ToList();
    }

    public List<string> Names()
    {
        if (!Enabled) return [];
        lock (gate) return byDomain.Keys.ToList();
    }

    public List<ClusterIngress> Clusters()
    {
        if (!Enabled) return [];
        lock (gate) return clusters.Values.ToList();
    }

    public List<ProxyRoute> Routes()
    {
        if (!Enabled) return [];
        List<DomainTarget> targets;
        lock (gate) targets = byDomain.Values.ToList();
        List<ProxyRoute> routes = [];
        foreach (var target in targets)
        {
            var address = target.IsHostNetwork ? IPAddress.Loopback : target.Address;
            if (address is not null && Port(target) is { } port) routes.Add(new ProxyRoute(target.Domain, $"http://{address}:{port}"));
        }
        return routes;
    }

    public void Choose(string containerId, int? port)
    {
        lock (gate)
        {
            var name = byDomain.Values.FirstOrDefault(t => t.ContainerId == containerId)?.ContainerName;
            if (name is null) return;
            if (port is { } p) chosen[name] = p;
            else chosen.Remove(name);
            Changed();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(choicesFile)!);
                File.WriteAllText(choicesFile, JsonSerializer.Serialize(chosen, AgentApiJson.Default.DictionaryStringInt32));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"cannot keep the port choice: {e.Message}"); }
        }
    }

    // On each container event this watch sees, for what else follows the engine's containers.
    public event Action? ContainersChanged;

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var docker = connect();
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                await Refresh(docker, ct);
                ContainersChanged?.Invoke();
                await foreach (var e in docker.EventsAsync(["container"], since, ct))
                    if (e.Action is "start" or "die" or "destroy" or "rename")
                    {
                        await Refresh(docker, ct);
                        ContainersChanged?.Invoke();
                    }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    async Task Refresh(DockerClient docker, CancellationToken ct)
    {
        Dictionary<string, DomainTarget> next = [];
        Dictionary<string, ClusterIngress> ingress = [];
        List<(string Cluster, string Name, IPAddress Address, bool Http, bool Https)> balancers = [];
        foreach (var summary in (await docker.ListContainersAsync(all: false, ct)).Where(c => c.State == "running"))
        {
            var inspect = await docker.GetNodeAsync($"containers/{Uri.EscapeDataString(summary.Id)}/json", ct);
            var address = inspect["NetworkSettings"]?["Networks"]?.AsObject()
                .Select(n => n.Value?["IPAddress"]?.GetValue<string>())
                .FirstOrDefault(ip => ip is { Length: > 0 });
            var exposed = inspect["Config"]?["ExposedPorts"]?.AsObject()
                .Select(p => p.Key.Split('/'))
                .Where(p => p.Length == 2 && p[1] == "tcp" && int.TryParse(p[0], out _))
                .Select(p => int.Parse(p[0])).ToList() ?? [];
            var hostNetwork = summary.HostConfig?.NetworkMode == "host";
            var labels = summary.Labels;
            if (labels?.GetValueOrDefault(ClusterLabel) is { Length: > 0 } cluster)
            {
                var onKind = inspect["NetworkSettings"]?["Networks"]?["kind"]?["IPAddress"]?.GetValue<string>() is { Length: > 0 } ip ? IPAddress.Parse(ip) : null;
                if (onKind is not null) balancers.Add((cluster, summary.Name, onKind, exposed.Contains(80), exposed.Contains(443)));
                continue;
            }
            // A kind node serves Kubernetes on its own ports, not a site, and the cluster answers by <host>.<cluster> instead;
            // Dockyard's own containers are not for browsing either.
            if (labels is not null && (labels.ContainsKey("io.x-k8s.kind.cluster") || labels.ContainsKey(Services.ServiceLabel))) continue;
            foreach (var domain in DomainNames.For(summary.Name, labels?.GetValueOrDefault("com.docker.compose.project"), labels?.GetValueOrDefault("com.docker.compose.service")))
                // The first running replica answers for a Compose service's shared name.
                next.TryAdd(domain, new DomainTarget(summary.Id, summary.Name, domain, address is null ? null : IPAddress.Parse(address),
                    inspect["State"]?["Pid"]?.GetValue<int>(), exposed, DomainNames.LabelPort(labels), hostNetwork));
        }
        foreach (var cluster in balancers.GroupBy(b => b.Cluster))
            ingress[cluster.Key] = PickIngress(cluster.Key, cluster);
        lock (gate)
        {
            byDomain = next;
            clusters = ingress;
        }
        Changed();
    }

    readonly SemaphoreSlim changes = new(0, 1);

    void Changed()
    {
        if (changes.CurrentCount == 0) try { changes.Release(); } catch (SemaphoreFullException) { }
    }

    // Returns when a container started, stopped or got another port chosen, or after the timeout: a port a container only
    // starts to listen on later shows up in no event.
    public Task WaitForChange(TimeSpan timeout, CancellationToken ct) => changes.WaitAsync(timeout, ct);
}
