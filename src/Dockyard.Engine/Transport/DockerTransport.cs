using System.IO.Pipes;
using System.Net.Sockets;

namespace Dockyard.Engine.Transport;

public static class DockerTransport
{
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static HttpMessageHandler CreateHandler(DockerEndpoint endpoint) => new SocketsHttpHandler
    {
        ConnectCallback = (_, ct) => Connect(endpoint, ct),
        ConnectTimeout = ConnectTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        UseProxy = false,
        AutomaticDecompression = System.Net.DecompressionMethods.None,
    };

    public static async ValueTask<Stream> Connect(DockerEndpoint endpoint, CancellationToken ct)
    {
        switch (endpoint.Kind)
        {
            case EndpointKind.NamedPipe:
                var pipe = new NamedPipeClientStream(".", endpoint.Address, PipeDirection.InOut, PipeOptions.Asynchronous);
                try
                {
                    await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, ct);
                    return pipe;
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }

            case EndpointKind.Unix:
                return await ConnectSocket(new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified),
                    s => s.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Address), ct));

            case EndpointKind.Tcp:
                return await ConnectSocket(new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true },
                    s => s.ConnectAsync(endpoint.Address, endpoint.Port, ct));

            default:
                throw new ArgumentOutOfRangeException(nameof(endpoint), endpoint.Kind, null);
        }
    }

    static async Task<Stream> ConnectSocket(Socket socket, Func<Socket, ValueTask> connect)
    {
        try
        {
            await connect(socket);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
