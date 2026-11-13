using System.Text.RegularExpressions;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Clusters;

// kind keeps no state of its own: a cluster is the containers carrying its labels, so the engine's list is the truth.
static partial class KindClusters
{
    public const string ClusterLabel = "io.x-k8s.kind.cluster";
    public const string RoleLabel = "io.x-k8s.kind.role";
    const int ApiServerPort = 6443;

    public static List<KindCluster> From(IEnumerable<ContainerItem> containers) => containers
        .Where(c => c.Labels.ContainsKey(ClusterLabel))
        .GroupBy(c => c.Labels[ClusterLabel])
        .Select(g =>
        {
            var nodes = g.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            string Role(ContainerItem c) => c.Labels.GetValueOrDefault(RoleLabel, "");
            // With several control planes kind puts a load balancer in front, and that is the address in the kubeconfig.
            var front = nodes.FirstOrDefault(c => Role(c) == "external-load-balancer") ?? nodes.FirstOrDefault(c => Role(c) == "control-plane");
            var port = front?.Ports.FirstOrDefault(p => p.ContainerPort == ApiServerPort)?.HostPort;
            var version = nodes.Where(c => Role(c).StartsWith("control-plane", StringComparison.Ordinal))
                .Select(c => NodeVersion().Match(c.Image)).FirstOrDefault(m => m.Success)?.Groups["v"].Value;
            return new KindCluster(g.Key, nodes.Select(c => new KindNode(c.Name, Role(c), c.State)).ToList(), version, port);
        })
        .OrderBy(c => c.Name, StringComparer.Ordinal)
        .ToList();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    [GeneratedRegex(@"^[a-z0-9.-]+$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^(?:[^@]*/)?node:(?<v>v\d+\.\d+\.\d+)")]
    private static partial Regex NodeVersion();
}
