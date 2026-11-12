using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dockyard.Agent.Tests;

/// <summary>What Windows reaches the WSL engine through with NAT networking. docker exec, attach and run without -d
/// half-close their side once stdin is done, and the engine only then writes what the command printed.</summary>
public sealed class WindowsEngineTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dy-we").FullName;
    string EngineSocket => Path.Combine(dir, "e.sock");

    public void Dispose() => Directory.Delete(dir, recursive: true);

    // Like dockerd answering an exec: it reads the request to the end, which the client marks with a half-close, then writes.
    async Task FakeEngine(CancellationToken ct)
    {
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(EngineSocket));
        listener.Listen(4);
        using var engine = await listener.AcceptAsync(ct);
        var request = new MemoryStream();
        var buffer = new byte[1024];
        int n;
        while ((n = await engine.ReceiveAsync(buffer, ct)) > 0) request.Write(buffer, 0, n);
        await engine.SendAsync(Encoding.ASCII.GetBytes($"output after '{Encoding.ASCII.GetString(request.ToArray())}'"), ct);
        engine.Shutdown(SocketShutdown.Send);
    }

    (Socket Listener, int Port) Listen()
    {
        var listener = WindowsEngine.Listen(IPAddress.Loopback, 0);
        return (listener, ((IPEndPoint)listener.LocalEndPoint!).Port);
    }

    static async Task<string> Exchange(int port, string request, CancellationToken ct)
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        await client.SendAsync(Encoding.ASCII.GetBytes(request), ct);
        client.Shutdown(SocketShutdown.Send);
        var reply = new MemoryStream();
        var buffer = new byte[1024];
        int n;
        while ((n = await client.ReceiveAsync(buffer, ct)) > 0) reply.Write(buffer, 0, n);
        return Encoding.ASCII.GetString(reply.ToArray());
    }

    [Fact]
    public async Task What_the_engine_sends_after_the_client_half_closed_still_reaches_Windows()
    {
        var ct = TestContext.Current.CancellationToken;
        var engine = FakeEngine(ct);
        var (listener, port) = Listen();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = WindowsEngine.Serve(listener, IPAddress.Loopback, EngineSocket, cts.Token);

        Assert.Equal("output after 'POST /exec/1/start'", await Exchange(port, "POST /exec/1/start", ct));

        await engine;
        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task Only_Windows_is_let_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = WindowsEngine.Serve(listener, IPAddress.Parse("172.20.0.1"), EngineSocket, cts.Token);

        Assert.Equal("", await Exchange(port, "GET /_ping", ct));

        cts.Cancel();
        await serving;
    }
}
