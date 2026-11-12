using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

// What containers use as their DNS server: names Dockyard knows, and everything else from whatever the host resolves
// with, which is how lookups keep following the host's VPN and DNS settings as they change.
sealed class DnsServer(IPAddress listen, Func<string, IPAddress?> local, Func<string> readResolvConf, Action<string> log, int port = 53, Func<string, bool>? ownedZone = null)
{
    public const byte ServerFailure = 2;
    public const byte NameError = 3;
    static readonly TimeSpan UpstreamTimeout = TimeSpan.FromSeconds(3);

    public int Port { get; private set; } = port;

    public async Task Run(CancellationToken ct)
    {
        using var udp = new Socket(listen.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(listen, Port));
        Port = ((IPEndPoint)udp.LocalEndPoint!).Port;
        using var tcp = new Socket(listen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        tcp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        tcp.Bind(new IPEndPoint(listen, Port));
        tcp.Listen(64);
        log($"dns on {listen}:{Port}");
        await Task.WhenAll(ServeUdp(udp, ct), ServeTcp(tcp, ct));
    }

    async Task ServeUdp(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            var received = await socket.ReceiveFromAsync(buffer, any, ct);
            var query = buffer[..received.ReceivedBytes];
            var client = received.RemoteEndPoint;
            _ = Task.Run(async () =>
            {
                try
                {
                    if (await ReplyUdp(query, ct) is { } reply) await socket.SendToAsync(reply, client, ct);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException) { }
            }, ct);
        }
    }

    async Task<byte[]?> ReplyUdp(byte[] query, CancellationToken ct)
    {
        if (DnsPacket.Question(query) is not { } question) return null;
        if (local(question.Name) is { } address) return DnsPacket.Answer(query, question, address);
        if (ownedZone?.Invoke(question.Name) == true) return DnsPacket.Failure(query, question, NameError);
        foreach (var upstream in ResolvConf.Upstreams(readResolvConf(), listen))
        {
            try
            {
                using var socket = new Socket(upstream.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                await socket.ConnectAsync(upstream, 53, ct);
                await socket.SendAsync(query, ct);
                var buffer = new byte[4096];
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(UpstreamTimeout);
                var n = await socket.ReceiveAsync(buffer, timeout.Token);
                return buffer[..n];
            }
            catch (Exception e) when (e is SocketException || e is OperationCanceledException && !ct.IsCancellationRequested) { }
        }
        return DnsPacket.Failure(query, question, ServerFailure);
    }

    async Task ServeTcp(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                using var owned = client;
                try { await ReplyTcp(owned, ct); }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException) { }
            }, ct);
        }
    }

    // A truncated UDP answer sends the client here, so upstream gets asked over TCP too.
    async Task ReplyTcp(Socket client, CancellationToken ct)
    {
        await using var stream = new NetworkStream(client);
        while (await UdpFrame.ReadAsync(stream, ct) is { } query)
        {
            if (DnsPacket.Question(query) is not { } question) return;
            byte[]? reply = null;
            if (local(question.Name) is { } address) reply = DnsPacket.Answer(query, question, address);
            else if (ownedZone?.Invoke(question.Name) == true) reply = DnsPacket.Failure(query, question, NameError);
            else
            {
                foreach (var upstream in ResolvConf.Upstreams(readResolvConf(), listen))
                {
                    try
                    {
                        using var socket = new Socket(upstream.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(UpstreamTimeout);
                        await socket.ConnectAsync(upstream, 53, timeout.Token);
                        await using var server = new NetworkStream(socket);
                        await server.WriteAsync(UdpFrame.Encode(query), timeout.Token);
                        reply = await UdpFrame.ReadAsync(server, timeout.Token);
                        if (reply is not null) break;
                    }
                    catch (Exception e) when (e is SocketException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { }
                }
            }
            await stream.WriteAsync(UdpFrame.Encode(reply ?? DnsPacket.Failure(query, question, ServerFailure)), ct);
        }
    }
}
