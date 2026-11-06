using System.Net;
using System.Net.Sockets;

namespace Dockyard.Core;

// Windows sends "localhost" to ::1 first, and mirrored WSL carries only IPv4 loopback to Linux, so a connection there
// hangs instead of failing over. Listening on ::1 and passing connections to 127.0.0.1 answers it right away.
sealed class LoopbackForwarder(Action<string> log) : IDisposable
{
    readonly Dictionary<int, Socket> listeners = [];
    readonly HashSet<int> unavailable = [];
    readonly object gate = new();

    public IReadOnlyCollection<int> Ports { get { lock (gate) return listeners.Keys.ToList(); } }

    public void Apply(IEnumerable<int> wanted)
    {
        var ports = wanted.ToHashSet();
        lock (gate)
        {
            foreach (var port in listeners.Keys.Where(p => !ports.Contains(p)).ToList())
            {
                listeners.Remove(port, out var socket);
                socket!.Dispose();
            }
            unavailable.IntersectWith(ports);
            foreach (var port in ports.Where(p => !listeners.ContainsKey(p) && !unavailable.Contains(p)))
            {
                var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
                    socket.Listen(128);
                }
                catch (SocketException e)
                {
                    socket.Dispose();
                    // Something else has [::1]:port, and it keeps it. Saying so once is enough.
                    unavailable.Add(port);
                    log($"localhost:{port} stays with what already listens on [::1]:{port} ({e.SocketErrorCode})");
                    continue;
                }
                listeners[port] = socket;
                _ = Accept(socket, port);
            }
        }
    }

    static async Task Accept(Socket listener, int port)
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptAsync();
                _ = Forward(client, port);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    static async Task Forward(Socket client, int port)
    {
        using (client)
        using (var target = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                await target.ConnectAsync(IPAddress.Loopback, port);
                await Task.WhenAll(Copy(client, target), Copy(target, client));
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { }
        }
    }

    // One side finishing is passed on as a half-close, and the other direction keeps flowing until it ends too.
    static async Task Copy(Socket from, Socket to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReceiveAsync(buffer);
                if (n == 0) break;
                await to.SendAsync(buffer.AsMemory(0, n));
            }
        }
        catch (SocketException) { }
        try { to.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => Apply([]);
}
