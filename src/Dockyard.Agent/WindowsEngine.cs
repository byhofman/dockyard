using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

// With WSL's NAT networking, Windows reached 127.0.0.1:2375 through WSL's localhost relay, and that relay drops what the
// engine sends after the client half-closes its side, as docker exec, attach and run without -d do: they printed nothing.
// So the engine is served to Windows here, on WSL's own address and to its gateway alone, which is Windows, and the tray
// carries Windows' 127.0.0.1:2375 to it. Mirrored networking shares loopback for real and keeps dockerd's own listener.
static class WindowsEngine
{
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

    public static async Task Serve(Socket listener, IPAddress windows, string dockerSocket, CancellationToken ct)
    {
        using (listener)
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptAsync(ct);
                    // Anything else on WSL's network, such as a container reaching this address, would get root on the engine.
                    if (client.RemoteEndPoint is not IPEndPoint { Address: var from } || !from.Equals(windows))
                    {
                        client.Dispose();
                        continue;
                    }
                    _ = Task.Run(async () =>
                    {
                        using var connection = new SocketConnection(client);
                        try { await Agent.ProxyDocker(connection, dockerSocket, ct); }
                        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
                    }, CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { }
        }
    }
}
