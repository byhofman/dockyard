using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

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
