using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dockyard.Agent.Tests;

/// <summary>What Windows reaches the WSL engine and the agent API through. Only with the tray's token, and docker exec,
/// attach and run without -d half-close their side once stdin is done, after which the engine writes what the command printed.</summary>
public sealed class WindowsEngineTests : IDisposable
{
    const string Token = "0123456789abcdef0123456789abcdef";
    readonly string dir = Directory.CreateTempSubdirectory("dy-we").FullName;
    readonly AgentApi api = Api();

    static AgentApi Api()
    {
        var table = new DomainTable(() => throw new InvalidOperationException("no engine here"), Path.GetTempFileName(), _ => { });
        return new AgentApi(table, new DomainProxy(table, _ => { }));
    }
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

    Task Serve(Socket listener, Func<IPAddress, bool> isWindows, CancellationToken ct) =>
        WindowsEngine.Serve(listener, isWindows, () => Token, EngineSocket, api, ct);

    static async Task<string> Exchange(int port, string request, CancellationToken ct, string header = $"{Token} engine\n")
    {
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port, ct);
        await client.SendAsync(Encoding.ASCII.GetBytes(header + request), ct);
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
        var serving = Serve(listener, IPAddress.IsLoopback, cts.Token);

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
        var serving = Serve(listener, IPAddress.Parse("172.20.0.1").Equals, cts.Token);

        // Closing with the request still unread resets the connection on macOS and Windows and ends it on Linux; either
        // way nothing of the engine's comes back.
        string reply;
        try { reply = await Exchange(port, "GET /_ping", ct); }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { reply = ""; }
        Assert.Equal("", reply);

        cts.Cancel();
        await serving;
    }

    [Theory]
    [InlineData("GET /_ping")]
    [InlineData("ffffffffffffffffffffffffffffffff engine\nGET /_ping")]
    [InlineData("0123456789abcdef0123456789abcdef\nGET /_ping")]
    public async Task Windows_without_the_token_gets_nothing(string request)
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = Serve(listener, IPAddress.IsLoopback, cts.Token);

        string reply;
        try { reply = await Exchange(port, request, ct, header: ""); }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) { reply = ""; }
        Assert.Equal("", reply);

        cts.Cancel();
        await serving;
    }

    [Fact]
    public async Task The_token_also_opens_the_agent_API()
    {
        var ct = TestContext.Current.CancellationToken;
        var (listener, port) = Listen();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serving = Serve(listener, IPAddress.IsLoopback, cts.Token);

        var reply = await Exchange(port, "{\"op\":\"nonsense\"}\n", ct, header: $"{Token} api\n");

        Assert.Contains("unknown request", reply);
        cts.Cancel();
        await serving;
    }

    [Theory]
    [InlineData(null, Token)]
    [InlineData("", Token)]
    [InlineData(Token + " engine", null)]
    [InlineData(Token + " engine", "")]
    [InlineData(Token + " shell", Token)]
    [InlineData(Token + " engine extra", Token)]
    public void A_header_without_the_right_token_and_a_known_service_is_refused(string? header, string? token) =>
        Assert.Null(WindowsEngine.Admit(header, token));

    [Fact]
    public void The_right_token_admits_the_service_it_names()
    {
        Assert.Equal("engine", WindowsEngine.Admit($"{Token} engine", Token));
        Assert.Equal("api", WindowsEngine.Admit($"{Token} api", Token));
    }
}
