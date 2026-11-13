namespace Dockyard.Engine;

public static class PublishedPorts
{
    // One entry per protocol and port; a wildcard bind wins over a loopback bind.
    public static List<PublishedPort> From(IEnumerable<ContainerSummary> containers) => containers
        .Where(c => c.State == "running")
        .SelectMany(c => c.Ports)
        .Where(p => p.PublicPort is > 0 && p.Type is "tcp" or "udp")
        .Select(p => new PublishedPort(p.Type, Normalize(p.IP), p.PublicPort!.Value))
        .GroupBy(p => (p.Protocol, p.Port))
        .Select(g => g.Any(p => p.HostIp == "0.0.0.0") ? g.First() with { HostIp = "0.0.0.0" } : g.First())
        .OrderBy(p => p.Port).ThenBy(p => p.Protocol)
        .ToList();

    static string Normalize(string? ip) => ip switch
    {
        null or "" or "0.0.0.0" or "::" => "0.0.0.0",
        "::1" => "127.0.0.1",
        _ => ip,
    };
}
