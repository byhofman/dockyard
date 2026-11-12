using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dockyard.Agent;

// Addresses that only exist inside the engine's Linux, so they cannot clash with anything on the user's networks.
static class GuestAddresses
{
    public static readonly IPAddress Dns = IPAddress.Parse("198.19.250.1");
    public static readonly IPAddress Host = IPAddress.Parse("198.19.250.254");
    public const string Subnet = "198.19.250.0/24";
    public const int HostProxyPort = 2378;
    public const int DomainHttpPort = 2380;
    public const int DomainHttpsPort = 2381;
    public const int RelayHttpPort = 2382;
    public const int RelayHttpsPort = 2383;
    public const int RelayDnsPort = 2353;
    public const int ContainerDnsPort = 2354;

    // Where the host's browser finds domains where it cannot use the engine's own address: all of 127/8 is loopback,
    // and an address nothing else uses cannot clash with a server on 127.0.0.1:80, such as another Docker app's.
    public static readonly IPAddress HostLoopback = IPAddress.Parse("127.77.0.1");

    static readonly HashSet<string> HostNames = ["host.docker.internal", "gateway.docker.internal"];

    public static IPAddress? Resolve(string name) => HostNames.Contains(name) ? Host : null;

    // What host.docker.internal never reaches on the host: Docker's own TCP ports and Dockyard's, whose services are for
    // the host and the tray, not for any image a container runs.
    public static readonly IReadOnlySet<int> RefusedOnHost = new HashSet<int>
    {
        2375, 2376, 2377, HostProxyPort, 2379, DomainHttpPort, DomainHttpsPort, RelayHttpPort, RelayHttpsPort, RelayDnsPort, ContainerDnsPort,
    };
}

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

interface IHostDialer
{
    Task<IConnection> Dial(int port, CancellationToken ct);
}

// A connection to the host's loopback, from the engine's Linux.
sealed class VsockHostDialer : IHostDialer
{
    const uint HostCid = 2;

    public async Task<IConnection> Dial(int port, CancellationToken ct)
    {
        var connection = await VsockTransport.Connect(HostCid, GuestAddresses.HostProxyPort, ct);
        try
        {
            await connection.Stream.WriteAsync(Encoding.ASCII.GetBytes($"tcp {port}\n"), ct);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

sealed class TcpHostDialer(IPAddress host, IReadOnlySet<int>? refused = null) : IHostDialer
{
    public async Task<IConnection> Dial(int port, CancellationToken ct)
    {
        if (refused?.Contains(port) == true) throw new SocketException((int)SocketError.ConnectionRefused);
        var socket = new Socket(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return new SocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

static class WslHost
{
    // Mirrored networking gives WSL an interface named loopback0, and 127.0.0.1 then reaches Windows. With NAT only the
    // gateway reaches Windows, and only services that listen on more than loopback and that its firewall lets through.
    public static IPAddress Address(bool mirrored, string procNetRoute) =>
        mirrored ? IPAddress.Loopback : DefaultGateway(procNetRoute) ?? IPAddress.Loopback;

    public static IPAddress? DefaultGateway(string procNetRoute)
    {
        foreach (var line in procNetRoute.Split('\n').Skip(1))
        {
            var fields = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 2 && fields[1] == "00000000" && uint.TryParse(fields[2], System.Globalization.NumberStyles.HexNumber, null, out var gateway))
                return new IPAddress(gateway);
        }
        return null;
    }
}

sealed class HostProxy(IHostDialer dialer, Func<Socket, IPEndPoint?> originalDestination, Action<string> log, int port = GuestAddresses.HostProxyPort)
{
    public int Port { get; private set; } = port;

    public async Task Run(CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Bind(new IPEndPoint(IPAddress.Any, Port));
        listener.Listen(64);
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        log($"host proxy on {Port}");
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                using var connection = new SocketConnection(client);
                try { await Handle(client, connection, ct); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
            }, ct);
        }
    }

    async Task Handle(Socket client, SocketConnection connection, CancellationToken ct)
    {
        // Anything that came here without being redirected from the host address connected to this port directly.
        if (originalDestination(client) is not { } target || !target.Address.Equals(GuestAddresses.Host)) return;
        using var host = await dialer.Dial(target.Port, ct);
        await Pump.Both(connection, host, ct);
    }

    // getsockopt(SOL_IP, SO_ORIGINAL_DST): what the connection was addressed to before iptables redirected it.
    public static IPEndPoint? OriginalDestination(Socket socket)
    {
        const int SolIp = 0;
        const int SoOriginalDst = 80;
        var address = new byte[16];
        try
        {
            if (socket.GetRawSocketOption(SolIp, SoOriginalDst, address) < 8) return null;
        }
        catch (SocketException)
        {
            return null;
        }
        return new IPEndPoint(new IPAddress(address.AsSpan(4, 4)), address[2] << 8 | address[3]);
    }
}
