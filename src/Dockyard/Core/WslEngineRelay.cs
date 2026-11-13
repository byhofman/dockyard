using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Dockyard.Core;

// The Linux engine and its agent API as Windows programs reach them: named pipes that only this user can open, carried to
// dockyard-agent with the token it was given. A TCP port on Windows' loopback would be open to every user of the PC and
// to web pages through DNS rebinding, and the engine is root on what it mounts of C:.
// Message mode, as dockerd's own pipes have: docker's CloseWrite on a pipe is an empty message, which is passed on as a
// half-close, and docker exec and attach only get their output after it.
[SupportedOSPlatform("windows")]
sealed class WslEngineRelay(IPAddress gate, string token) : IDisposable
{
    readonly CancellationTokenSource lifetime = new();

    public IPAddress Gate { get; } = gate;

    // False when another program already holds one of the names: whatever connects there would not reach this engine.
    public bool Start()
    {
        var pipes = new List<NamedPipeServerStream>();
        try
        {
            foreach (var name in new[] { Paths.LinuxPipeName, Paths.LinuxAgentPipeName }) pipes.Add(Create(name, first: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var pipe in pipes) pipe.Dispose();
            return false;
        }
        _ = Serve(pipes[0], Paths.LinuxPipeName, "engine");
        _ = Serve(pipes[1], Paths.LinuxAgentPipeName, "api");
        return true;
    }

    static NamedPipeServerStream Create(string name, bool first) => new(name, PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Message,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    Task Serve(NamedPipeServerStream first, string name, string service) =>
        AcceptLoop.Run(first, () => Create(name, first: false), (pipe, ct) => pipe.WaitForConnectionAsync(ct), pipe => _ = Relay(pipe, service), lifetime.Token);

    async Task Relay(NamedPipeServerStream pipe, string service)
    {
        using (pipe)
        using (var engine = new Socket(Gate.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                await engine.ConnectAsync(Gate, Paths.LinuxPort, lifetime.Token);
                await engine.SendAsync(Encoding.ASCII.GetBytes($"{token} {service}\n"), lifetime.Token);
                await TcpRelay.Carry(pipe, engine, lifetime.Token);
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    public bool EngineAnswers(int timeoutMs = 500) => Answers(Gate, Paths.LinuxPort, timeoutMs);

    public static bool Answers(IPAddress address, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            return client.ConnectAsync(address, port).Wait(timeoutMs) && client.Connected;
        }
        catch { return false; }
    }

    public void Dispose() => lifetime.Cancel();
}

static class AcceptLoop
{
    // The next instance exists before a connection is handled, so a client never finds the name missing. A client that
    // left before its connection was taken fails only that instance: ending the loop then would leave a name whose only
    // instances are busy, and docker waits for a free one without end.
    public static async Task Run<T>(T first, Func<T> create, Func<T, CancellationToken, Task> waitForClient, Action<T> handle, CancellationToken ct)
        where T : IDisposable
    {
        var waiting = first;
        try
        {
            while (true)
            {
                try
                {
                    await waitForClient(waiting, ct);
                }
                catch (IOException)
                {
                    waiting.Dispose();
                    waiting = create();
                    continue;
                }
                var connected = waiting;
                waiting = create();
                handle(connected);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
        {
            waiting.Dispose();
        }
    }
}

static partial class WslEngineGate
{
    public const string TokenPath = "/etc/dockyard/windows.token";

    // What `[ -d /sys/class/net/loopback0 ] && echo mirrored || ip -4 -o addr show eth0` prints in the distro: with mirrored
    // networking the agent is on the loopback Windows shares, with NAT on WSL's own address; nothing gives null.
    public static IPAddress? AddressFromIp(string output) =>
        output.Trim() == "mirrored" ? IPAddress.Loopback
        : InetAddress().Match(output) is { Success: true } m && IPAddress.TryParse(m.Groups["a"].Value, out var a) ? a : null;

    // In this user's local app data, which other users of the PC cannot read; made once and kept, so a tray that starts
    // while an engine runs still gets in.
    public static string Token(string file)
    {
        if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: 64 } kept && kept.All(char.IsAsciiHexDigitLower)) return kept;
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, token);
        return token;
    }

    [GeneratedRegex(@"\binet (?<a>\d+\.\d+\.\d+\.\d+)/")]
    private static partial Regex InetAddress();
}
