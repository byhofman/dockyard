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
}
