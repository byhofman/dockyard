using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dockyard.Agent;

readonly record struct DnsQuestion(string Name, ushort Type, int End)
{
    public const ushort A = 1;
}

static class DnsPacket
{
    const int HeaderLength = 12;

    public static DnsQuestion? Question(ReadOnlySpan<byte> packet)
    {
        if (packet.Length <= HeaderLength || BinaryPrimitives.ReadUInt16BigEndian(packet[4..]) != 1) return null;
        var name = new StringBuilder();
        var i = HeaderLength;
        while (true)
        {
            if (i >= packet.Length) return null;
            int length = packet[i++];
            if (length == 0) break;
            // A question never needs a compression pointer, and a name is at most 255 bytes.
            if (length > 63 || i + length > packet.Length || name.Length > 255) return null;
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(packet.Slice(i, length)).ToLowerInvariant());
            i += length;
        }
        if (i + 4 > packet.Length) return null;
        return new DnsQuestion(name.ToString(), BinaryPrimitives.ReadUInt16BigEndian(packet[i..]), i + 4);
    }

    // An address for an A question; other questions about a known name get an empty answer, not an error, so that a
    // client asking AAAA and A at once keeps the A.
    public static byte[] Answer(ReadOnlySpan<byte> query, DnsQuestion question, IPAddress address)
    {
        var hasAnswer = question.Type == DnsQuestion.A;
        var reply = new byte[question.End + (hasAnswer ? 16 : 0)];
        Head(query, question, reply, rcode: 0);
        if (hasAnswer)
        {
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(6), 1);
            var answer = reply.AsSpan(question.End);
            answer[0] = 0xC0;
            answer[1] = HeaderLength;
            BinaryPrimitives.WriteUInt16BigEndian(answer[2..], DnsQuestion.A);
            BinaryPrimitives.WriteUInt16BigEndian(answer[4..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(answer[6..], 5);
            BinaryPrimitives.WriteUInt16BigEndian(answer[10..], 4);
            address.TryWriteBytes(answer[12..], out _);
        }
        return reply;
    }

    public static byte[] Failure(ReadOnlySpan<byte> query, DnsQuestion question, byte rcode)
    {
        var reply = new byte[question.End];
        Head(query, question, reply, rcode);
        return reply;
    }

    static void Head(ReadOnlySpan<byte> query, DnsQuestion question, Span<byte> reply, byte rcode)
    {
        query[..question.End].CopyTo(reply);
        reply[2] = (byte)(0x80 | (query[2] & 0x01));
        reply[3] = (byte)(0x80 | rcode);
        reply[4] = 0;
        reply[5] = 1;
        reply[6..12].Clear();
    }
}

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

// What containers use as their DNS server: names Dockyard knows, and everything else from whatever the host resolves
// with, which is how lookups keep following the host's VPN and DNS settings as they change.
sealed class DnsServer(IPAddress listen, Func<string, IPAddress?> local, Func<string> readResolvConf, Action<string> log, int port = 53)
{
    public const byte ServerFailure = 2;
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
