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

    static readonly HashSet<string> HostNames = ["host.docker.internal", "gateway.docker.internal"];

    public static IPAddress? Resolve(string name) => HostNames.Contains(name) ? Host : null;
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
    }

    public static async Task Apply(Func<string, string[], Task<int>> run, Action<string> log)
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

        foreach (var command in Commands())
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
