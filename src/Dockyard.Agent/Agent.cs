using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Agent;

sealed partial class Agent(ITransport transport, Action<string> log, AgentApi? api = null)
{
    internal const string DockerSocket = "/var/run/docker.sock";
    static readonly TimeSpan MemoryInterval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan TrimInterval = TimeSpan.FromHours(1);

    readonly object controlLock = new();
    static readonly TimeSpan HostNetworkInterval = TimeSpan.FromSeconds(2);
    static readonly IReadOnlySet<int> OwnPorts = new HashSet<int> { GuestAddresses.HostProxyPort, GuestAddresses.DomainHttpPort, GuestAddresses.DomainHttpsPort, 53 };
    List<PublishedPort> ports = [];
    List<PublishedPort> published = [];
    volatile bool hasHostNetwork;
    StreamWriter? control;

    public async Task Run(CancellationToken ct)
    {
        await Task.WhenAll(
            Serve(Ports.Docker, ProxyDocker, ct),
            Serve(Ports.Forward, Forward, ct),
            Serve(Ports.Control, Control, ct),
            Serve(Ports.Api, (client, token) => api is null ? Task.CompletedTask : api.Handle(client.Stream, token), ct),
            WatchPorts(ct),
            WatchHostNetwork(ct),
            ReportMemory(ct),
            Trim(ct));
    }

    async Task Serve(int port, Func<IConnection, CancellationToken, Task> handle, CancellationToken ct)
    {
        IListener listener;
        try
        {
            listener = transport.Listen(port);
        }
        catch (SocketException e)
        {
            log($"cannot listen on {port}: {e.Message}");
            throw;
        }
        using var owned = listener;
        log($"listening on {port}");
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptAsync(ct);
            _ = Task.Run(async () =>
            {
                using var connection = client;
                try { await handle(connection, ct); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
                catch (Exception e) { log($"port {port}: {e.Message}"); }
            }, ct);
        }
    }

    static Task ProxyDocker(IConnection client, CancellationToken ct) => ProxyDocker(client, DockerSocket, ct);

    internal static async Task ProxyDocker(IConnection client, string dockerSocket, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var docker = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await docker.ConnectAsync(new UnixDomainSocketEndPoint(dockerSocket), ct);
            }
            catch (SocketException) when (attempt < 100)
            {
                docker.Dispose();
                await Task.Delay(200, ct);
                continue;
            }
            catch
            {
                docker.Dispose();
                throw;
            }
            using var engine = new SocketConnection(docker);
            await Pump.Both(client, engine, ct);
            return;
        }
    }

    // A server in a --network host container may listen on IPv6 only.
    static async Task<SocketConnection> ConnectTcp(int port, CancellationToken ct)
    {
        try { return await ConnectTcp(IPAddress.Loopback, port, ct); }
        catch (SocketException) { return await ConnectTcp(IPAddress.IPv6Loopback, port, ct); }
    }

    static async Task<SocketConnection> ConnectTcp(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(address, port, ct);
            return new SocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    static async Task Forward(IConnection client, CancellationToken ct)
    {
        var line = await ReadLine(client.Stream, ct);
        if (line is null || ForwardHeader.Parse(line) is not { } header) return;

        if (header.Protocol == ForwardProtocol.Tcp)
        {
            using var target = await ConnectTcp(header.Port, ct);
            await Pump.Both(client, target, ct);
            return;
        }

        using var udp = new UdpClient();
        udp.Connect(IPAddress.Loopback, header.Port);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var replies = Task.Run(async () =>
        {
            while (!linked.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(linked.Token);
                await client.Stream.WriteAsync(UdpFrame.Encode(result.Buffer), linked.Token);
            }
        }, linked.Token);
        while (await UdpFrame.ReadAsync(client.Stream, ct) is { } datagram) await udp.SendAsync(datagram, ct);
        await linked.CancelAsync();
        try { await replies; } catch (Exception) { }
    }

    // The header is read byte by byte so no payload bytes are buffered away from the pump.
    static async Task<string?> ReadLine(Stream stream, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 64)
        {
            if (await stream.ReadAsync(one, ct) == 0) return null;
            if (one[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        return null;
    }

    async Task Control(IConnection client, CancellationToken ct)
    {
        var writer = new StreamWriter(client.Stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        lock (controlLock) control = writer;
        log("control connected");
        Send(new ControlMessage { Type = "ports", Ports = ports });

        using var reader = new StreamReader(client.Stream);
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var message = JsonSerializer.Deserialize(line, ControlJson.Default.ControlMessage);
                switch (message?.Type)
                {
                    case "clock" when message.UnixNs is { } ns:
                        SetClock(ns);
                        break;
                    case "stop":
                        await Stop();
                        return;
                }
            }
        }
        finally
        {
            lock (controlLock) if (control == writer) control = null;
            log("control disconnected");
        }
    }

    void Send(ControlMessage message)
    {
        var line = JsonSerializer.Serialize(message, ControlJson.Default.ControlMessage);
        lock (controlLock)
        {
            try { control?.WriteLine(line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { control = null; }
        }
    }

    async Task WatchPorts(CancellationToken ct)
    {
        using var docker = new DockerClient(DockerEndpoint.Parse("unix://" + DockerSocket));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                await Refresh(docker, ct);
                await foreach (var e in docker.EventsAsync(["container"], since, ct))
                    if (e.Action is "start" or "die" or "destroy" or "pause" or "unpause") await Refresh(docker, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await Task.Delay(1000, ct);
            }
        }
    }

    async Task Refresh(DockerClient docker, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync(all: false, ct);
        published = PublishedPorts.From(containers);
        hasHostNetwork = containers.Any(c => c.State == "running" && c.HostConfig?.NetworkMode == "host");
        ReportPorts();
    }

    void ReportPorts()
    {
        var next = ListeningPorts.Merge(published, hasHostNetwork ? ListeningPorts.Read(ReadIfThere, OwnPorts) : []);
        lock (controlLock)
        {
            if (next.SequenceEqual(ports)) return;
            ports = next;
        }
        log("ports: " + string.Join(", ", next.Select(p => $"{p.Protocol}/{p.Port}")));
        Send(new ControlMessage { Type = "ports", Ports = next });
    }

    static string? ReadIfThere(string file)
    {
        try { return File.ReadAllText(file); }
        catch (IOException) { return null; }
    }

    // A server in a --network host container can start any time after its container did, and only the kernel knows.
    // Nothing is polled unless such a container runs.
    async Task WatchHostNetwork(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(HostNetworkInterval, ct);
            if (hasHostNetwork) ReportPorts();
        }
    }

    async Task ReportMemory(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var info = MemoryInfo.Parse(await File.ReadAllTextAsync("/proc/meminfo", ct));
            Send(new ControlMessage { Type = "memory", TotalKb = info.TotalKb, AvailableKb = info.AvailableKb });
            await Task.Delay(MemoryInterval, ct);
        }
    }

    static async Task Trim(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TrimInterval, ct);
            try { await Run("fstrim", "/var/lib/docker"); } catch (Exception) { }
        }
    }

    void SetClock(long unixNs)
    {
        var spec = new Timespec { Seconds = unixNs / 1_000_000_000, Nanoseconds = unixNs % 1_000_000_000 };
        if (clock_settime(0, ref spec) != 0) log($"clock_settime failed: {Marshal.GetLastPInvokeError()}");
        else log("clock set from host");
    }

    async Task Stop()
    {
        log("stop requested");
        foreach (var p in Process.GetProcessesByName("dockerd"))
        {
            using (p)
            {
                await Run("kill", "-TERM", p.Id.ToString());
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)).ContinueWith(_ => { });
            }
        }
        await Run("sync");
        await Run("poweroff");
    }

    static async Task Run(string file, params string[] args) => await Exit(file, args);

    public static async Task<int> Exit(string file, string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardError = true })!;
            // A rule that is already there or a command that fails is reported by the exit code, not on the console.
            await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 127;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int clock_settime(int clock, ref Timespec spec);
}
