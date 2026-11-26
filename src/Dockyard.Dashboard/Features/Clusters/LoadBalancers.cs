using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using PublishedPort = Dockyard.Dashboard.Store.PublishedPort;

namespace Dockyard.Dashboard.Features.Clusters;

sealed record LoadBalancer(string Service, string Address, IReadOnlyList<PublishedPort> Ports, string Container = "")
{
    public bool HasAddress => Address.Length > 0;
    public string PortsText => string.Join(", ", Ports.Select(p => $"{p.ContainerPort}/{p.Protocol.ToLowerInvariant()}"));
}

// What runs for a cluster's load balancer, which the engine's agent keeps running: kind's cloud provider's Envoy containers,
// and the forwarder of the cluster's fixed ports.
static class LoadBalancers
{
    public const string ClusterLabel = KindCloudProvider.ClusterLabel;
    public const string NameLabel = KindCloudProvider.NameLabel;
    public const string ForwarderClusterLabel = KindForwarder.ClusterLabel;

    public static bool IsProxy(ContainerItem item) => item.Labels.ContainsKey(ClusterLabel) || item.Labels.ContainsKey(ForwarderClusterLabel);

    public static List<LoadBalancer> For(string cluster, IEnumerable<ContainerItem> containers) => containers
        .Where(c => c.Labels.GetValueOrDefault(ClusterLabel) == cluster)
        .Select(c => new LoadBalancer(
            c.Labels.GetValueOrDefault(NameLabel) is { } name && name.StartsWith(cluster + "/", StringComparison.Ordinal) ? name[(cluster.Length + 1)..] : c.Name,
            c.Addresses.GetValueOrDefault("kind") ?? "",
            c.Ports.Where(p => p.ContainerPort != KindCloudProvider.EnvoyAdminPort).OrderBy(p => p.ContainerPort).ToList(),
            c.Name))
        .OrderBy(l => l.Service, StringComparer.Ordinal)
        .ToList();

    // Under Load balancer, one line per fixed port, then what failed: for this cluster, or for all of them.
    public static string Notes(ClusterStatus status, IReadOnlyList<string> errors) => string.Join("\n",
        status.Forwards.Select(f => $"127.0.0.1:{f.Port} → {f.Service}")
            .Concat(status.Taken.Select(t => $"Port {t.Port} is used by cluster {t.Cluster}; its service keeps the port Docker picked"))
            .Concat(status.Busy.Select(p => $"Port {p} is in use by another program; its service keeps the port Docker picked. Dockyard tries again when the cluster starts."))
            .Concat(Failures(status, errors)));

    // For the footer's activity, when it changes.
    public static string Activity(ClusterStatus status, IReadOnlyList<string> errors) => string.Join(", ",
        status.Forwards.Select(f => $"127.0.0.1:{f.Port} → {f.Service}").Concat(status.Busy.Select(p => $"port {p} in use by another program")).Concat(Failures(status, errors)));

    static IEnumerable<string> Failures(ClusterStatus status, IReadOnlyList<string> errors) =>
        (status.Error is { } error ? errors.Prepend(error) : errors).Select(e => $"Load balancers for kind: {e}");
}
