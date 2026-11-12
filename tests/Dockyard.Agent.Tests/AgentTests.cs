using System.Net.Sockets;
using System.Text.Json;
using Dockyard.Engine;

namespace Dockyard.Agent.Tests;

public sealed class AgentTests
{
    [Theory]
    [InlineData("tcp 8080", "Tcp", 8080)]
    [InlineData("udp 53\r", "Udp", 53)]
    public void Parses_forward_headers(string line, string protocol, int port)
    {
        var header = ForwardHeader.Parse(line);
        Assert.NotNull(header);
        Assert.Equal(Enum.Parse<ForwardProtocol>(protocol), header.Value.Protocol);
        Assert.Equal(port, header.Value.Port);
        Assert.Equal(line.Trim(), header.Value.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("tcp")]
    [InlineData("sctp 80")]
    [InlineData("tcp 0")]
    [InlineData("tcp 70000")]
    public void Rejects_bad_forward_headers(string line) => Assert.Null(ForwardHeader.Parse(line));

    [Fact]
    public async Task Udp_frames_round_trip()
    {
        var stream = new MemoryStream();
        stream.Write(UdpFrame.Encode([1, 2, 3]));
        stream.Write(UdpFrame.Encode([]));
        stream.Position = 0;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal([1, 2, 3], await UdpFrame.ReadAsync(stream, ct));
        Assert.Empty((await UdpFrame.ReadAsync(stream, ct))!);
        Assert.Null(await UdpFrame.ReadAsync(stream, ct));
    }

    [Fact]
    public void Published_ports_dedupe_and_prefer_wildcard_binds()
    {
        ContainerSummary C(string state, params (string Ip, int Port, string Type)[] ports) => new()
        {
            State = state,
            Ports = ports.Select(p => new PortMapping { IP = p.Ip, PrivatePort = 80, PublicPort = p.Port, Type = p.Type }).ToList(),
        };

        var result = PublishedPorts.From(
        [
            C("running", ("0.0.0.0", 8080, "tcp"), ("::", 8080, "tcp")),
            C("running", ("127.0.0.1", 5432, "tcp"), ("0.0.0.0", 53, "udp")),
            C("running", ("::1", 6379, "tcp")),
            C("exited", ("0.0.0.0", 9000, "tcp")),
        ]);

        Assert.Equal(
        [
            new PublishedPort("udp", "0.0.0.0", 53),
            new PublishedPort("tcp", "127.0.0.1", 5432),
            new PublishedPort("tcp", "127.0.0.1", 6379),
            new PublishedPort("tcp", "0.0.0.0", 8080),
        ], result);
    }

    [Fact]
    public void Parses_meminfo()
    {
        var info = MemoryInfo.Parse("MemTotal:        8123456 kB\nMemFree:         1000 kB\nMemAvailable:    4000000 kB\n");
        Assert.Equal(8123456, info.TotalKb);
        Assert.Equal(4000000, info.AvailableKb);
    }

    [Fact]
    public void Vsock_address_layout_matches_sockaddr_vm()
    {
        Assert.Equal([40, 0, 0, 0, 0x47, 0x09, 0, 0, 3, 0, 0, 0, 0, 0, 0, 0], VsockTransport.Address(3, 2375));
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], VsockTransport.Address(VsockTransport.AnyCid, 1)[8..12]);
    }

    [Fact]
    public async Task Pump_passes_half_close_and_keeps_the_reply_direction_open()
    {
        var ct = TestContext.Current.CancellationToken;
        var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        server.Start();
        var port = ((System.Net.IPEndPoint)server.LocalEndpoint).Port;

        var echo = Task.Run(async () =>
        {
            using var s = await server.AcceptSocketAsync(ct);
            var buffer = new byte[64];
            var received = new List<byte>();
            int n;
            while ((n = await s.ReceiveAsync(buffer, ct)) > 0) received.AddRange(buffer[..n]);
            await s.SendAsync(received.ToArray(), ct);
            s.Shutdown(SocketShutdown.Send);
        }, ct);

        var (client, agentSide) = await Pair(ct);
        var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await target.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);
        var pump = Pump.Both(new SocketConnection(agentSide), new SocketConnection(target), ct);

        await client.SendAsync("hello"u8.ToArray(), ct);
        client.Shutdown(SocketShutdown.Send);
        var reply = new byte[16];
        var count = await client.ReceiveAsync(reply, ct);
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(reply, 0, count));
        await pump;
        await echo;
        server.Stop();
        client.Dispose();
    }

    static async Task<(Socket Client, Socket Server)> Pair(CancellationToken ct)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var accept = listener.AcceptSocketAsync(ct).AsTask();
        await client.ConnectAsync((System.Net.IPEndPoint)listener.LocalEndpoint, ct);
        var server = await accept;
        listener.Stop();
        return (client, server);
    }

    [Fact]
    public void Control_messages_are_camel_case_without_nulls()
    {
        var json = JsonSerializer.Serialize(new ControlMessage { Type = "memory", TotalKb = 10, AvailableKb = 5 }, ControlJson.Default.ControlMessage);
        Assert.Equal("""{"type":"memory","totalKb":10,"availableKb":5}""", json);

        var ports = JsonSerializer.Serialize(new ControlMessage { Type = "ports", Ports = [new("tcp", "0.0.0.0", 80)] }, ControlJson.Default.ControlMessage);
        Assert.Equal("""{"type":"ports","ports":[{"protocol":"tcp","hostIp":"0.0.0.0","port":80}]}""", ports);
    }

    [Fact]
    public async Task Lines_are_read_one_by_one_and_an_endless_one_ends_the_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var normal = new StreamReader(new MemoryStream("{\"op\":\"domains\"}\r\nsecond"u8.ToArray()));
        Assert.Equal("{\"op\":\"domains\"}", await Lines.Read(normal, ct));
        Assert.Equal("second", await Lines.Read(normal, ct));
        Assert.Null(await Lines.Read(normal, ct));

        using var endless = new StreamReader(new MemoryStream(new byte[Lines.Max + 1].Select(_ => (byte)'a').ToArray()));
        await Assert.ThrowsAsync<IOException>(() => Lines.Read(endless, ct));
    }
}
