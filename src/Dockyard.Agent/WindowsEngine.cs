using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Dockyard.Agent;

// The engine and the agent API as Windows reaches them. Everything Windows can reach on this port, every user and every
// web page through DNS rebinding can reach too, and the engine is root on the distro and on what it mounts of C:. So a
// connection is only served after it names the token the tray wrote to the distro, which only the tray's user can read,
// and the tray offers it to Windows on a named pipe for that user alone.
// With NAT networking the port is on WSL's own address and taken from its gateway, which is Windows: WSL's localhost relay
// loses what the engine sends after a client half-closes, as docker exec and attach do. With mirrored networking
// loopback is shared with Windows for real.
static class WindowsEngine
{
    public const string TokenFile = "/etc/dockyard/windows.token";
    public const string EngineService = "engine";
    public const string ApiService = "api";
    const int MaxHeader = 128;
    static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(5);

    public static Socket Listen(IPAddress address, int port = Ports.Docker)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(new IPEndPoint(address, port));
            listener.Listen(64);
            return listener;
        }
        catch
        {
            listener.Dispose();
            throw;
        }
    }

    public static async Task Serve(Socket listener, Func<IPAddress, bool> isWindows, Func<string?> token, string dockerSocket, AgentApi api, CancellationToken ct)
    {
        using (listener)
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptAsync(ct);
                    // Anything else on WSL's network, such as a container reaching this address, is not Windows.
                    if (client.RemoteEndPoint is not IPEndPoint { Address: var from } || !isWindows(from))
                    {
                        client.Dispose();
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        using var connection = new SocketConnection(client);
                        try
                        {
                            switch (Admit(await ReadHeader(connection.Stream, ct), token()))
                            {
                                case EngineService: await Agent.ProxyDocker(connection, dockerSocket, ct); break;
                                case ApiService: await api.Handle(connection.Stream, ct); break;
                            }
                        }
                        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    // "<token> <service>": the service asked for, or null when the token is missing or wrong.
    public static string? Admit(string? header, string? token)
    {
        if (header?.Split(' ') is not [var offered, var service and (EngineService or ApiService)] || string.IsNullOrEmpty(token)) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offered), Encoding.ASCII.GetBytes(token)) ? service : null;
    }

    public static string? ReadToken() => File.Exists(TokenFile) ? File.ReadAllText(TokenFile).Trim() : null;

    // Byte by byte, so nothing of what follows the header is read ahead and lost to the engine.
    static async Task<string?> ReadHeader(Stream stream, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeaderTimeout);
        var header = new byte[MaxHeader];
        var one = new byte[1];
        for (var length = 0; length < MaxHeader; length++)
        {
            if (await stream.ReadAsync(one, timeout.Token) == 0) return null;
            if (one[0] == '\n') return Encoding.ASCII.GetString(header, 0, length);
            header[length] = one[0];
        }
        return null;
    }
}
