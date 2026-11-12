using System.Net;
using System.Net.Sockets;
using System.Text;
using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class WslEngineRelayTests
{
    [Theory]
    [InlineData("3: eth0    inet 172.27.48.10/20 brd 172.27.63.255 scope global eth0\\       valid_lft forever preferred_lft forever\n", "172.27.48.10")]
    [InlineData("", null)]
    public void Finds_the_engine_from_what_ip_prints(string output, string? address) =>
        Assert.Equal(address, WslEngineRelay.AddressFromIp(output)?.ToString());

    // docker exec: the client half-closes once it has nothing to send, and only then does the engine write the output.
    [Fact]
    public async Task What_the_engine_sends_after_the_client_half_closed_comes_through()
    {
        var ct = TestContext.Current.CancellationToken;
        using var engine = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        engine.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        engine.Listen(4);
        var enginePort = ((IPEndPoint)engine.LocalEndPoint!).Port;
        var answering = Task.Run(async () =>
        {
            using var c = await engine.AcceptAsync(ct);
            var request = new MemoryStream();
            var buffer = new byte[1024];
            int n;
            while ((n = await c.ReceiveAsync(buffer, ct)) > 0) request.Write(buffer, 0, n);
            await c.SendAsync(Encoding.ASCII.GetBytes("exec-works"), ct);
            c.Shutdown(SocketShutdown.Send);
        }, ct);

        using var relay = new WslEngineRelay(IPAddress.Loopback, enginePort, listenPort: 0);
        Assert.True(relay.Start());

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, relay.ListeningPort, ct);
        await client.SendAsync(Encoding.ASCII.GetBytes("POST /exec/1/start"), ct);
        client.Shutdown(SocketShutdown.Send);
        var reply = new MemoryStream();
        var buf = new byte[1024];
        int read;
        while ((read = await client.ReceiveAsync(buf, ct)) > 0) reply.Write(buf, 0, read);

        Assert.Equal("exec-works", Encoding.ASCII.GetString(reply.ToArray()));
        await answering;
    }

    [Fact]
    public void A_port_someone_else_holds_is_left_to_them()
    {
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        other.Listen(1);
        using var relay = new WslEngineRelay(IPAddress.Parse("172.27.48.10"), listenPort: ((IPEndPoint)other.LocalEndPoint!).Port);
        Assert.False(relay.Start());
    }
}
