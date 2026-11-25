using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using PublishedPort = Dockyard.Dashboard.Store.PublishedPort;

namespace Dockyard.Dashboard.Features.Clusters;

sealed record LoadBalancer(string Service, string Address, IReadOnlyList<PublishedPort> Ports, string Container = "")
{
    public bool HasAddress => Address.Length > 0;
    public string PortsText => string.Join(", ", Ports.Select(p => $"{p.ContainerPort}/{p.Protocol.ToLowerInvariant()}"));
}

// kind's cloud provider, one container per engine while there are clusters: it gives LoadBalancer services an address
// and starts an Envoy container per service whose ports are published on the host, so they work from Windows and the Mac.
static class LoadBalancers
{
    public const string Name = "dockyard-cloud-provider-kind";
    public const string Image = "registry.k8s.io/cloud-provider-kind/cloud-controller-manager:v0.12.0@sha256:1b97632e5dfec8f83c7e970cf18cedb9e6b707e272011d712cabf6ab59d323c9";
    public const string ClusterLabel = KindLabels.LoadBalancerCluster;
    public const string NameLabel = "io.x-k8s.cloud-provider-kind.loadbalancer.name";
    const string SetupLabel = "dockyard.cloud-provider-kind.setup";
    static readonly string[] Flags = ["--enable-lb-port-mapping", "--enable-default-ingress=false", "--gateway-channel=disabled"];
    public static readonly string Setup = $"{Image} {string.Join(' ', Flags)}";
    const int EnvoyAdminPort = 10000;

    public static string[] RunArgs(string context) =>
    [
        "--context", context, "run", "-d", "--name", Name, "--restart", "unless-stopped", "--network", "kind",
        "--label", "dockyard.helper=cloud-provider-kind", "--label", $"{SetupLabel}={Setup}", "-v", "/var/run/docker.sock:/var/run/docker.sock",
        Image, .. Flags,
    ];

    public static bool IsProxy(ContainerItem item) => item.Labels.ContainsKey(ClusterLabel) || item.Labels.ContainsKey(ForwarderClusterLabel);

    public static List<LoadBalancer> For(string cluster, IEnumerable<ContainerItem> containers) => containers
        .Where(c => c.Labels.GetValueOrDefault(ClusterLabel) == cluster)
        .Select(c => new LoadBalancer(
            c.Labels.GetValueOrDefault(NameLabel) is { } name && name.StartsWith(cluster + "/", StringComparison.Ordinal) ? name[(cluster.Length + 1)..] : c.Name,
            c.Addresses.GetValueOrDefault("kind") ?? "",
            c.Ports.Where(p => p.ContainerPort != EnvoyAdminPort).OrderBy(p => p.ContainerPort).ToList(),
            c.Name))
        .OrderBy(l => l.Service, StringComparer.Ordinal)
        .ToList();

    public const string ForwarderPrefix = "dockyard-lb-";
    public const string ForwarderClusterLabel = KindLabels.ForwarderCluster;
    const string ForwardsLabel = "dockyard.lb.forwards";
    const string ForwarderImage = "alpine:3.22@sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f";

    public sealed record Forward(int Port, string Proxy, string Service);

    // Which of a cluster's ports goes to which service: the first, by name, whose load balancer has that port. A port another
    // cluster took first goes in taken, and one no service has yet waits.
    public static (List<Forward> Forwards, List<(int Port, string Cluster)> Taken) PlanForwards(
        IReadOnlyList<int> ports, IEnumerable<LoadBalancer> balancers, IReadOnlyDictionary<int, string> claimed, string cluster)
    {
        var forwards = new List<Forward>();
        var taken = new List<(int, string)>();
        foreach (var port in ports)
        {
            if (claimed.TryGetValue(port, out var other) && other != cluster) { taken.Add((port, other)); continue; }
            if (balancers.FirstOrDefault(b => b.Ports.Any(p => p.ContainerPort == port && p.Protocol.Equals("tcp", StringComparison.OrdinalIgnoreCase))) is { } b)
                forwards.Add(new Forward(port, b.Container, b.Service));
        }
        return (forwards, taken);
    }

    public static string Describe(IEnumerable<Forward> forwards) => string.Join(',', forwards.Select(f => $"{f.Port}={f.Proxy}"));

    // socat per port to the service's Envoy container on kind's network; published on 127.0.0.1 only.
    public static string ForwarderScript(IEnumerable<Forward> forwards) =>
        "command -v socat >/dev/null || apk add --no-cache socat >/dev/null\n"
        + string.Concat(forwards.Select(f => $"socat TCP-LISTEN:{f.Port},fork,reuseaddr TCP:{f.Proxy}:{f.Port} &\n"))
        + "trap 'exit 0' TERM INT\nwait\n";

    public static string[] ForwarderArgs(string context, string cluster, IReadOnlyList<Forward> forwards) =>
    [
        "--context", context, "run", "-d", "--name", ForwarderPrefix + cluster, "--restart", "unless-stopped", "--network", "kind",
        "--label", $"{ForwarderClusterLabel}={cluster}", "--label", $"{ForwardsLabel}={Describe(forwards)}",
        .. forwards.SelectMany(f => new[] { "-p", $"127.0.0.1:{f.Port}:{f.Port}" }),
        ForwarderImage, "sh", "-c", ForwarderScript(forwards),
    ];

    // docker's words for a host port something else has: "port is already allocated" or "address already in use".
    public static int? BusyPort(string error)
    {
        var match = System.Text.RegularExpressions.Regex.Match(error, @"(?:127\.0\.0\.1|0\.0\.0\.0)?:(\d+)[^\d]*(?:failed: port is already allocated|bind: address already in use|Only one usage)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    // Makes the cluster's forwarder do what is planned; returns the ports another program holds, which are left out.
    public static async Task<List<int>> SyncForwarder(DockerClient client, string context, string cluster, IReadOnlyList<Forward> planned, bool running)
    {
        var name = ForwarderPrefix + cluster;
        var current = (await client.ListContainersAsync(all: true)).FirstOrDefault(c => c.Names.Contains("/" + name));
        var forwards = planned.ToList();
        var busy = new List<int>();
        if (current is not null && current.Labels?.GetValueOrDefault(ForwardsLabel) == Describe(forwards) && forwards.Count > 0)
        {
            if (running && current.State != "running") await client.StartContainerAsync(current.Id);
            return busy;
        }
        if (current is not null) await client.RemoveContainerAsync(current.Id, force: true);
        while (forwards.Count > 0 && running)
        {
            var (exit, _, error) = await Kind.SharedStorage.Docker(ForwarderArgs(context, cluster, forwards));
            if (exit == 0) break;
            await Kind.SharedStorage.Docker(["--context", context, "rm", "-f", name]);
            if (BusyPort(error) is not { } port || forwards.All(f => f.Port != port))
                throw new InvalidOperationException($"Could not start {name}: {error.Trim()}");
            busy.Add(port);
            forwards.RemoveAll(f => f.Port == port);
        }
        return busy;
    }

    public enum Step { None, Run, Start, Replace, Remove }

    // What to do with the controller: one is wanted while clusters exist and the switch is on, and an older image or setup is replaced.
    public static Step Plan(bool wanted, ContainerSummary? current) => (wanted, current) switch
    {
        (true, null) => Step.Run,
        (true, { } c) when c.Labels?.GetValueOrDefault(SetupLabel) != Setup => Step.Replace,
        (true, { State: not "running" }) => Step.Start,
        (false, not null) => Step.Remove,
        _ => Step.None,
    };

    public static async Task Sync(DockerClient client, string context, bool wanted, Action<string>? onLine = null)
    {
        var current = (await client.ListContainersAsync(all: true)).FirstOrDefault(c => c.Names.Contains("/" + Name));
        var step = Plan(wanted, current);
        if (step is Step.Remove or Step.Replace) await client.RemoveContainerAsync(current!.Id, force: true);
        if (step is Step.Start) await client.StartContainerAsync(current!.Id);
        if (step is Step.Run or Step.Replace)
        {
            var exit = await Resources.CliRunner.Run(RunArgs(context), line => onLine?.Invoke(line));
            if (exit != 0) throw new InvalidOperationException($"Could not start kind's cloud provider (docker run exited with {exit}).");
        }
    }
}
