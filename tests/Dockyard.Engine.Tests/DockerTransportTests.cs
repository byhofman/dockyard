using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Dockyard.Engine.Tests.Support;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine.Tests;

public sealed class DockerTransportTests
{
    static async Task<string> Ping(DockerEndpoint endpoint)
    {
        using var http = new HttpClient(DockerTransport.CreateHandler(endpoint)) { BaseAddress = new Uri("http://docker/") };
        return await http.GetStringAsync("_ping", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tcp_endpoint_reaches_listener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync();
                await CannedHttp.ServeOnce(client.GetStream());
            });
            Assert.Equal("OK", await Ping(DockerEndpoint.Parse($"tcp://127.0.0.1:{port}")));
            await server;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task Unix_endpoint_reaches_listener()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dy-{Guid.NewGuid():N}.sock");
        using var listen = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listen.Bind(new UnixDomainSocketEndPoint(path));
        listen.Listen(1);
        try
        {
            var server = Task.Run(async () =>
            {
                using var socket = await listen.AcceptAsync();
                using var stream = new NetworkStream(socket);
                await CannedHttp.ServeOnce(stream);
            });
            Assert.Equal("OK", await Ping(DockerEndpoint.Parse("unix://" + path)));
            await server;
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Named_pipe_endpoint_reaches_listener()
    {
        var name = $"dockyard-test-{Guid.NewGuid():N}";
        var server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync();
            await CannedHttp.ServeOnce(pipe);
        });
        Assert.Equal("OK", await Ping(DockerEndpoint.Parse($"npipe:////./pipe/{name}")));
        await server;
    }

    [Fact]
    public async Task Unreachable_tcp_endpoint_throws_HttpRequestException()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsAsync<HttpRequestException>(() => Ping(DockerEndpoint.Parse($"tcp://127.0.0.1:{port}")));
    }
}
