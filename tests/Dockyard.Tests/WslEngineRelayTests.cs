using System.Net;
using System.Net.Sockets;
using System.Text;
using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class WslEngineRelayTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dy-gate").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Theory]
    [InlineData("3: eth0    inet 172.27.48.10/20 brd 172.27.63.255 scope global eth0\\       valid_lft forever preferred_lft forever\n", "172.27.48.10")]
    [InlineData("mirrored\n", "127.0.0.1")]
    [InlineData("", null)]
    public void Finds_the_agent_from_what_the_distro_prints(string output, string? address) =>
        Assert.Equal(address, WslEngineGate.AddressFromIp(output)?.ToString());

    [Fact]
    public void The_token_is_made_once_and_kept()
    {
        var file = Path.Combine(dir, "Dockyard", "engine.token");

        var token = WslEngineGate.Token(file);

        Assert.Matches("^[0-9a-f]{64}$", token);
        Assert.Equal(token, WslEngineGate.Token(file));
    }

    [Fact]
    public void A_token_file_that_is_not_a_token_is_replaced()
    {
        var file = Path.Combine(dir, "engine.token");
        File.WriteAllText(file, "guessable");

        Assert.Matches("^[0-9a-f]{64}$", WslEngineGate.Token(file));
    }

    // docker exec: the client half-closes once it has nothing to send, and only then does the engine write the output.
    [Fact]
    public async Task What_the_engine_sends_after_the_client_is_done_sending_comes_through()
    {
        var ct = TestContext.Current.CancellationToken;
        using var engineListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        engineListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        engineListener.Listen(4);
        var answering = Task.Run(async () =>
        {
            using var c = await engineListener.AcceptAsync(ct);
            var request = new MemoryStream();
            var buffer = new byte[1024];
            int n;
            while ((n = await c.ReceiveAsync(buffer, ct)) > 0) request.Write(buffer, 0, n);
            await c.SendAsync(Encoding.ASCII.GetBytes($"output after '{Encoding.ASCII.GetString(request.ToArray())}'"), ct);
            c.Shutdown(SocketShutdown.Send);
        }, ct);

        using var clientListener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        clientListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        clientListener.Listen(1);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync((IPEndPoint)clientListener.LocalEndPoint!, ct);
        using var relayed = await clientListener.AcceptAsync(ct);
        using var engine = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await engine.ConnectAsync((IPEndPoint)engineListener.LocalEndPoint!, ct);
        var carrying = TcpRelay.Carry(new NetworkStream(relayed), engine, ct);

        await client.SendAsync(Encoding.ASCII.GetBytes("POST /exec/1/start"), ct);
        client.Shutdown(SocketShutdown.Send);
        await carrying;
        relayed.Shutdown(SocketShutdown.Send);
        var reply = new MemoryStream();
        var buf = new byte[1024];
        int read;
        while ((read = await client.ReceiveAsync(buf, ct)) > 0) reply.Write(buf, 0, read);

        Assert.Equal("output after 'POST /exec/1/start'", Encoding.ASCII.GetString(reply.ToArray()));
        await answering;
    }
}
