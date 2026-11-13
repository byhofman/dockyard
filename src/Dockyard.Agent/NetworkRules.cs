using System.Net;

namespace Dockyard.Agent;

static class NetworkRules
{
    // Only containers and the engine's own Linux may use these addresses; on a Linux host or mirrored WSL the machine's
    // real network interfaces are right there too, and nothing on the LAN may reach the host's loopback through them.
    public static IEnumerable<string[]> Commands()
    {
        var host = GuestAddresses.Host.ToString();
        var port = GuestAddresses.HostProxyPort.ToString();
        yield return ["iptables", "-I", "INPUT", "-d", GuestAddresses.Subnet, "-j", "DOCKYARD-INPUT"];
        foreach (var iface in new[] { "docker+", "br-+" })
            yield return ["iptables", "-t", "nat", "-I", "PREROUTING", "-i", iface, "-d", host, "-p", "tcp", "-j", "REDIRECT", "--to-ports", port];
        yield return ["iptables", "-t", "nat", "-I", "OUTPUT", "-d", host, "-p", "tcp", "-j", "REDIRECT", "--to-ports", port];

        // Domains seen from inside: containers resolve them to the DNS address, and ports 80 and 443 there mean the proxy.
        var dns = GuestAddresses.Dns.ToString();
        foreach (var (from, to) in new[] { (80, GuestAddresses.DomainHttpPort), (443, GuestAddresses.DomainHttpsPort) })
        {
            foreach (var iface in new[] { "docker+", "br-+" })
                yield return ["iptables", "-t", "nat", "-I", "PREROUTING", "-i", iface, "-d", dns, "-p", "tcp", "--dport", from.ToString(), "-j", "REDIRECT", "--to-ports", to.ToString()];
            yield return ["iptables", "-t", "nat", "-I", "OUTPUT", "-d", dns, "-p", "tcp", "--dport", from.ToString(), "-j", "REDIRECT", "--to-ports", to.ToString()];
        }
    }

    // A Linux host: the machine's own programs reach the proxy by redirect. Binding 127.77.0.1:80 itself would make every
    // later "-p 80:80" and every web server on *:80 fail, since Linux refuses a wildcard bind next to a specific one.
    public static IEnumerable<string[]> HostCommands()
    {
        var loopback = GuestAddresses.HostLoopback.ToString();
        foreach (var (from, to) in new[] { (80, GuestAddresses.DomainHttpPort), (443, GuestAddresses.DomainHttpsPort) })
            yield return ["iptables", "-t", "nat", "-I", "OUTPUT", "-d", loopback, "-p", "tcp", "--dport", from.ToString(), "-j", "REDIRECT", "--to-ports", to.ToString()];

        // A redirected packet arrives addressed to the bridge, not to the subnet DOCKYARD-INPUT guards, so ufw's and
        // other default-deny INPUT rules would drop it. These ports refuse whatever was not redirected to them.
        var redirected = string.Join(',', GuestAddresses.HostProxyPort, GuestAddresses.DomainHttpPort, GuestAddresses.DomainHttpsPort);
        foreach (var iface in new[] { "docker+", "br-+" })
            yield return ["iptables", "-I", "INPUT", "-i", iface, "-p", "tcp", "-m", "multiport", "--dports", redirected, "-j", "ACCEPT"];

        var dns = GuestAddresses.Dns.ToString();
        var target = $"{dns}:{GuestAddresses.ContainerDnsPort}";
        foreach (var protocol in new[] { "udp", "tcp" })
        {
            foreach (var iface in new[] { "docker+", "br-+" })
                yield return ["iptables", "-t", "nat", "-I", "PREROUTING", "-i", iface, "-d", dns, "-p", protocol, "--dport", "53", "-j", "DNAT", "--to-destination", target];
            yield return ["iptables", "-t", "nat", "-I", "OUTPUT", "-d", dns, "-p", protocol, "--dport", "53", "-j", "DNAT", "--to-destination", target];
        }
    }

    // Only the VM: there the Mac reaches the engine's Linux at its own address, and published ports never come in that
    // way (they go over vsock), so ports 80 and 443 on that interface are free to mean the proxy. Only from the Mac, the
    // gateway: other VMs on the same virtual network are not for the domains to answer.
    public static IEnumerable<string[]> VmCommands(IPAddress mac)
    {
        foreach (var (from, to) in new[] { (80, GuestAddresses.DomainHttpPort), (443, GuestAddresses.DomainHttpsPort) })
            yield return ["iptables", "-t", "nat", "-I", "PREROUTING", "-i", "eth0", "-s", mac.ToString(), "-p", "tcp", "--dport", from.ToString(), "-j", "REDIRECT", "--to-ports", to.ToString()];
    }

    public static async Task Apply(Func<string, string[], Task<int>> run, Action<string> log, IPAddress? vmHost = null, bool linuxHost = false)
    {
        foreach (var address in new[] { GuestAddresses.Dns, GuestAddresses.Host })
            await run("ip", ["addr", "replace", $"{address}/32", "dev", "lo"]);

        // -w: dockerd sets up its own rules at the same moment, and without it the loser of that lock just fails.
        Task<int> Iptables(params string[] args) => run("iptables", ["-w", .. args]);
        await Iptables("-N", "DOCKYARD-INPUT");
        await Iptables("-F", "DOCKYARD-INPUT");
        foreach (var iface in new[] { "lo", "docker+", "br-+" })
            await Iptables("-A", "DOCKYARD-INPUT", "-i", iface, "-j", "ACCEPT");
        await Iptables("-A", "DOCKYARD-INPUT", "-j", "DROP");

        foreach (var command in Commands().Concat(vmHost is not null ? VmCommands(vmHost) : []).Concat(linuxHost ? HostCommands() : []))
        {
            // -C is the same rule as -I at the same place; a restarted agent must not stack copies.
            if (await Iptables([.. command.Skip(1).Select(a => a == "-I" ? "-C" : a)]) == 0) continue;
            var code = await Iptables(command[1..]);
            if (code != 0) log($"{string.Join(' ', command)} exited with {code}");
        }
    }
}
