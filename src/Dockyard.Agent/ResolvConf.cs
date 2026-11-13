using System.Net;

namespace Dockyard.Agent;

static class ResolvConf
{
    // The agent's own address would be a loop.
    public static List<IPAddress> Upstreams(string text, IPAddress self) => text.Split('\n')
        .Select(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        .Where(p => p.Length >= 2 && p[0] == "nameserver" && IPAddress.TryParse(p[1], out _))
        .Select(p => IPAddress.Parse(p[1]))
        .Where(a => !a.Equals(self))
        .ToList();

    // On a Linux host, servers someone had put in daemon.json before Dockyard's agent took their place stay the ones
    // containers' names go to; the machine's own resolver is for everything else.
    public static string ForContainers(AgentMode mode, string? kept, string machine) =>
        mode == AgentMode.Host && !string.IsNullOrWhiteSpace(kept) ? kept : machine;
}
