using System.Text.RegularExpressions;

namespace Dockyard.Engine;

// A load balancer of a cluster as kind's cloud provider runs it: the Envoy container of one service, and its TCP ports.
public sealed record ClusterBalancer(string Service, string Container, IReadOnlyList<int> TcpPorts);

public sealed record PlannedForward(int Port, string Proxy, string Service);

// Per cluster, a container that publishes its fixed ports on the host's 127.0.0.1 and passes them on to the Envoy
// container of the service that has them.
public static partial class KindForwarder
{
    public const string Prefix = "dockyard-lb-";
    public const string ClusterLabel = "dockyard.lb.cluster";
    public const string ForwardsLabel = "dockyard.lb.forwards";
    const string Image = "alpine:3.22@sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f";

    // Which of a cluster's ports goes to which service: the first, by name, whose load balancer has that port. A port another
    // cluster took first goes in taken, and one no service has yet waits.
    public static (List<PlannedForward> Forwards, List<TakenPort> Taken) Plan(
        IReadOnlyList<int> ports, IEnumerable<ClusterBalancer> balancers, IReadOnlyDictionary<int, string> claimed, string cluster)
    {
        var ordered = balancers.OrderBy(b => b.Service, StringComparer.Ordinal).ToList();
        var forwards = new List<PlannedForward>();
        var taken = new List<TakenPort>();
        foreach (var port in ports)
        {
            if (claimed.TryGetValue(port, out var other) && other != cluster) { taken.Add(new TakenPort(port, other)); continue; }
            if (ordered.FirstOrDefault(b => b.TcpPorts.Contains(port)) is { } b) forwards.Add(new PlannedForward(port, b.Container, b.Service));
        }
        return (forwards, taken);
    }

    public static string Describe(IEnumerable<PlannedForward> forwards) => string.Join(',', forwards.Select(f => $"{f.Port}={f.Proxy}"));

    // socat per port to the service's Envoy container on kind's network.
    public static string Script(IEnumerable<PlannedForward> forwards) =>
        "command -v socat >/dev/null || apk add --no-cache socat >/dev/null\n"
        + string.Concat(forwards.Select(f => $"socat TCP-LISTEN:{f.Port},fork,reuseaddr TCP:{f.Proxy}:{f.Port} &\n"))
        + "trap 'exit 0' TERM INT\nwait\n";

    public static ServiceContainerSpec Spec(string cluster, IReadOnlyList<PlannedForward> forwards) => new(Prefix + cluster, Image, ["sh", "-c", Script(forwards)],
        new Dictionary<string, string> { [ClusterLabel] = cluster, [ForwardsLabel] = Describe(forwards) }, [], forwards.Select(f => f.Port).ToList(), SetupLabel: ForwardsLabel);

    // docker's words for a host port something else has: "port is already allocated" or "address already in use".
    public static int? BusyPort(string error)
    {
        var match = BusyPortPattern().Match(error);
        return match.Success && int.TryParse(match.Groups[1].Value, out var port) ? port : null;
    }

    [GeneratedRegex(@"(?:127\.0\.0\.1|0\.0\.0\.0):(\d+)(?:/tcp)?[^\d]*?(?:port is already allocated|address already in use|Only one usage)")]
    private static partial Regex BusyPortPattern();
}
