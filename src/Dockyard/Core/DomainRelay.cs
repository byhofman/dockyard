using System.Net;
using System.Net.Sockets;

namespace Dockyard.Core;

// On Windows only loopback is shared with WSL, also in mirrored networking, and a container's address is not reachable
// at all. So a name resolves to an address of Dockyard's own on Windows' loopback, and this passes on what arrives there.
// Nothing on 127.0.0.1:80 can clash with it, such as another Docker app's published port.
sealed class DomainRelay(Action<string> log) : IDisposable
{
    public static readonly IPAddress Address = IPAddress.Parse("127.77.0.1");
    const int HttpTarget = 2382;
    const int HttpsTarget = 2383;
    const int DnsTarget = 2353;

    readonly List<Socket> sockets = [];
    readonly CancellationTokenSource lifetime = new();

    public void Start()
    {
        foreach (var (port, target) in new[] { (80, HttpTarget), (443, HttpsTarget), (53, DnsTarget) })
        {
            try
            {
                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                sockets.Add(listener);
                listener.Bind(new IPEndPoint(Address, port));
                listener.Listen(128);
                _ = Accept(listener, target);
            }
            catch (SocketException e) { log($"Container domains: {Address}:{port} is not available ({e.SocketErrorCode})"); }
        }
        try
        {
            var dns = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sockets.Add(dns);
            dns.Bind(new IPEndPoint(Address, 53));
            _ = ServeDns(dns);
        }
        catch (SocketException e) { log($"Container domains: DNS on {Address} is not available ({e.SocketErrorCode})"); }
    }

    async Task Accept(Socket listener, int target)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptAsync(lifetime.Token);
                _ = TcpRelay.Forward(client, IPAddress.Loopback, target);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    // Windows asks over UDP, WSL only shares TCP on loopback: DNS is the same message over either, with a length in front.
    async Task ServeDns(Socket socket)
    {
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (true)
            {
                var received = await socket.ReceiveFromAsync(buffer, any, lifetime.Token);
                var query = buffer[..received.ReceivedBytes];
                var client = received.RemoteEndPoint;
                _ = Task.Run(async () =>
                {
                    if (await AskOverTcp(query, DnsTarget, lifetime.Token) is { } reply) await socket.SendToAsync(reply, client);
                });
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    internal static async Task<byte[]?> AskOverTcp(byte[] query, int port, CancellationToken ct)
    {
        try
        {
            using var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await upstream.ConnectAsync(IPAddress.Loopback, port, ct);
            await using var stream = new NetworkStream(upstream);
            await stream.WriteAsync(new[] { (byte)(query.Length >> 8), (byte)query.Length }, ct);
            await stream.WriteAsync(query, ct);
            var header = new byte[2];
            await stream.ReadExactlyAsync(header, ct);
            var reply = new byte[header[0] << 8 | header[1]];
            await stream.ReadExactlyAsync(reply, ct);
            return reply;
        }
        catch (Exception e) when (e is SocketException or IOException or EndOfStreamException or OperationCanceledException) { return null; }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var socket in sockets) socket.Dispose();
    }
}

static class TcpRelay
{
    public static async Task Forward(Socket client, IPAddress address, int port)
    {
        using (client)
        using (var target = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                await target.ConnectAsync(address, port);
                await Task.WhenAll(Copy(client, target), Copy(target, client));
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { }
        }
    }

    // A client done sending is passed on as a half-close, and what the engine still sends reaches it until the engine
    // ends. Disposing both afterwards is the caller's, which also ends the client's direction.
    public static async Task Carry(Stream client, Socket engine, CancellationToken ct)
    {
        var sending = Send();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await engine.ReceiveAsync(buffer, ct)) > 0)
        {
            await client.WriteAsync(buffer.AsMemory(0, n), ct);
            await client.FlushAsync(ct);
        }
        if (sending.IsCompleted) await sending;

        async Task Send()
        {
            var up = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await client.ReadAsync(up, ct)) > 0) await engine.SendAsync(up.AsMemory(0, read), ct);
                engine.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    // One side finishing is passed on as a half-close, and the other direction keeps flowing until it ends too.
    static async Task Copy(Socket from, Socket to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReceiveAsync(buffer);
                if (n == 0) break;
                await to.SendAsync(buffer.AsMemory(0, n));
            }
        }
        catch (SocketException) { }
        try { to.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }
}
