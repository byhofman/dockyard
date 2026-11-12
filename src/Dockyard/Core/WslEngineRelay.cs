using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

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

    // The next instance exists before a connection is handled, so a client never finds the name missing.
    async Task Serve(NamedPipeServerStream pipe, string name, string service)
    {
        try
        {
            while (true)
            {
                await pipe.WaitForConnectionAsync(lifetime.Token);
                var connected = pipe;
                pipe = Create(name, first: false);
                _ = Relay(connected, service);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            pipe.Dispose();
        }
    }

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
        catch (AggregateException e) when (e.InnerException is SocketException) { return false; }
        catch (SocketException) { return false; }
    }

    public void Dispose() => lifetime.Cancel();
}
