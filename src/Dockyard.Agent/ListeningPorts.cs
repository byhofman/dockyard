using Dockyard.Engine;

namespace Dockyard.Agent;

// What listens in the engine's own network namespace, which is where --network host containers put their servers.
static class ListeningPorts
{
    const string TcpListen = "0A";
    const string UdpUnconnected = "07";
    static readonly HashSet<string> ReachableV4 = ["00000000", "0100007F"];
    static readonly HashSet<string> ReachableV6 = ["00000000000000000000000000000000", "00000000000000000000000001000000"];

    // A line of /proc/net/tcp is "  0: 0100007F:1F90 00000000:0000 0A ...", with the port in hex. An address only
    // reachable inside (the agent's own, a bridge's) is nothing a server on the Mac's loopback could stand in for.
    public static IEnumerable<int> Parse(string procNet, bool udp)
    {
        foreach (var line in procNet.Split('\n').Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[3] != (udp ? UdpUnconnected : TcpListen)) continue;
            var local = fields[1].Split(':');
            if (local.Length != 2 || !(ReachableV4.Contains(local[0]) || ReachableV6.Contains(local[0]))) continue;
            if (int.TryParse(local[1], System.Globalization.NumberStyles.HexNumber, null, out var port) && port > 0) yield return port;
        }
    }

    // Always the Mac's loopback: --network host means the engine's Linux, and that says nothing about the LAN.
    public static IEnumerable<PublishedPort> Read(Func<string, string?> readFile, IReadOnlySet<int> own)
    {
        IEnumerable<PublishedPort> From(string file, string protocol, bool udp) =>
            readFile(file) is { } text ? Parse(text, udp).Where(p => !own.Contains(p)).Select(p => new PublishedPort(protocol, "127.0.0.1", p)) : [];
        return From("/proc/net/tcp", "tcp", false).Concat(From("/proc/net/tcp6", "tcp", false))
            .Concat(From("/proc/net/udp", "udp", true)).Concat(From("/proc/net/udp6", "udp", true)).Distinct();
    }

    // Published ports come first: they may bind more than loopback, and that stays their choice.
    public static List<PublishedPort> Merge(IEnumerable<PublishedPort> published, IEnumerable<PublishedPort> listening) =>
        published.Concat(listening).GroupBy(p => (p.Protocol, p.Port)).Select(g => g.First()).OrderBy(p => p.Port).ThenBy(p => p.Protocol).ToList();
}
