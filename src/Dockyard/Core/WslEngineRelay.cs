using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Dockyard.Core;

// Windows' 127.0.0.1:2375 for the WSL engine when WSL has NAT networking. WSL's own localhost relay loses what the engine
// sends after a client half-closes, which docker exec and attach do, so dockyard-agent serves the engine on WSL's address
// and this carries Windows' loopback to it, half-closes included. Mirrored networking needs none of it.
sealed partial class WslEngineRelay(IPAddress engine, int enginePort = Paths.LinuxPort, int listenPort = Paths.LinuxPort) : IDisposable
{
    readonly CancellationTokenSource lifetime = new();
    Socket? listener;

    public IPAddress Engine { get; } = engine;

    // Returns false when something else already has the port, such as WSL's relay for an engine from before this change.
    public bool Start()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (OperatingSystem.IsWindows()) socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, listenPort));
            socket.Listen(128);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return false;
        }
        listener = socket;
        _ = Accept(socket);
        return true;
    }

    public int ListeningPort => listener?.LocalEndPoint is IPEndPoint ep ? ep.Port : 0;

    async Task Accept(Socket socket)
    {
        try
        {
            while (true)
            {
                var client = await socket.AcceptAsync(lifetime.Token);
                _ = TcpRelay.Forward(client, Engine, enginePort);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    public bool EngineAnswers(int timeoutMs = 500) => Answers(Engine, enginePort, timeoutMs);

    public static bool Answers(IPAddress address, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            return client.ConnectAsync(address, port).Wait(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }

    // What `ip -4 -o addr show eth0` prints in the distro; nothing, as with mirrored networking, gives null.
    public static IPAddress? AddressFromIp(string output) =>
        InetAddress().Match(output) is { Success: true } m && IPAddress.TryParse(m.Groups["a"].Value, out var a) ? a : null;

    [GeneratedRegex(@"\binet (?<a>\d+\.\d+\.\d+\.\d+)/")]
    private static partial Regex InetAddress();

    public void Dispose()
    {
        lifetime.Cancel();
        listener?.Dispose();
        listener = null;
    }
}
