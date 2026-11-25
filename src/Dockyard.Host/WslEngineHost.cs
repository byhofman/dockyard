using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Shared;

namespace Dockyard.Host;

// The Linux engine in WSL as Windows needs it: the process that keeps the distro up, the pipes the docker CLI reaches it
// by, the relay for container domains on 127.77.0.1, and published ports on Windows' loopback. The Toolbox sets the engine
// up and asks for it to start or stop; this keeps it running whether the Toolbox is open or not.
[SupportedOSPlatform("windows")]
sealed class WslEngineHost(Action<string> log) : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    Process? keepAlive;
    WslEngineRelay? relay;
    DomainRelay? domains;
    CancellationTokenSource? ports;

    public async Task<string> Handle(string request)
    {
        await gate.WaitAsync();
        try
        {
            return request switch
            {
                "status" => Answers() ? HostPipe.Running : HostPipe.Stopped,
                "attach" => await Attach() ? HostPipe.Running : HostPipe.Stopped,
                "start" => await Start(),
                "stop" => await Stop(),
                "domains on" => StartDomains(),
                "domains off" => StopDomains(),
                _ => $"error unknown request {request}",
            };
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return $"error {e.Message}";
        }
        finally { gate.Release(); }
    }

    bool Answers()
    {
        if (relay is not { } active) return false;
        if (active.EngineAnswers(300)) return true;
        Release();
        return false;
    }

    // An engine that runs already, started before this one or by an older Toolbox, gets its pipes.
    public async Task<bool> Attach()
    {
        if (Answers()) return true;
        var running = await Run("wsl.exe", "-l", "--running", "-q");
        if (!running.Split('\n').Select(l => l.Trim().Trim('\0')).Contains(WslPaths.DistroName, StringComparer.OrdinalIgnoreCase)) return false;
        var ip = await Run("wsl.exe", "-d", WslPaths.DistroName, "-u", "root", "--", "sh", "-c", "[ -d /sys/class/net/loopback0 ] && echo mirrored || ip -4 -o addr show eth0");
        if (WslEngineGate.AddressFromIp(ip) is not { } address || !WslEngineRelay.Answers(address, WslPaths.LinuxPort, 500)) return false;
        var candidate = new WslEngineRelay(address, WslEngineGate.Token(WslPaths.EngineTokenFile));
        if (!candidate.Start())
        {
            candidate.Dispose();
            return false;
        }
        relay = candidate;
        FollowPorts();
        log("The Linux engine is reachable from Windows");
        return true;
    }

    async Task<string> Start()
    {
        if (await Attach()) return HostPipe.Ok;
        if (keepAlive is null || keepAlive.HasExited)
        {
            keepAlive?.Dispose();
            keepAlive = Process.Start(new ProcessStartInfo("wsl.exe", ["-d", WslPaths.DistroName, "-u", "root", "--", WslPaths.InitPath])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            }) ?? throw new InvalidOperationException("Could not start the WSL distro");
        }
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (keepAlive.HasExited) return $"error the {WslPaths.DistroName} distro stopped while starting (exit code {keepAlive.ExitCode})";
            if (await Attach()) return HostPipe.Ok;
            await Task.Delay(1000);
        }
        return "error the Linux engine did not answer within 90 seconds";
    }

    async Task<string> Stop()
    {
        if (keepAlive is { HasExited: false } || Answers())
            await Run("wsl.exe", "-d", WslPaths.DistroName, "-u", "root", "--", "sh", "-c",
                "pid=$(pidof dockerd) || exit 0; kill -TERM $pid; i=0; while pidof dockerd >/dev/null && [ $i -lt 150 ]; do sleep 0.2; i=$((i+1)); done; ! pidof dockerd >/dev/null");
        Release();
        if (keepAlive is { HasExited: false }) keepAlive.Kill(entireProcessTree: true);
        keepAlive?.Dispose();
        keepAlive = null;
        return HostPipe.Ok;
    }

    string StartDomains()
    {
        if (domains is not null) return HostPipe.Ok;
        domains = new DomainRelay(log);
        domains.Start();
        return HostPipe.Ok;
    }

    string StopDomains()
    {
        domains?.Dispose();
        domains = null;
        return HostPipe.Ok;
    }

    void Release()
    {
        ports?.Cancel();
        ports = null;
        relay?.Dispose();
        relay = null;
    }

    void FollowPorts()
    {
        ports?.Cancel();
        ports = new CancellationTokenSource();
        var token = ports.Token;
        _ = Task.Run(async () =>
        {
            using var forwarder = new LoopbackForwarder(log);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var docker = new DockerClient(DockerEndpoint.Parse(WslPaths.LinuxHost));
                    var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                    async Task Refresh() => forwarder.Apply(PublishedPorts.From(await docker.ListContainersAsync(all: false, token))
                        .Where(p => p.Protocol == "tcp").Select(p => p.Port));
                    await Refresh();
                    await foreach (var e in docker.EventsAsync(["container"], since, token))
                        if (e.Action is "start" or "die" or "destroy") await Refresh();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    forwarder.Apply([]);
                }
                try { await Task.Delay(TimeSpan.FromSeconds(3), token); }
                catch (OperationCanceledException) { }
            }
        }, token);
    }

    static async Task<string> Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var output = process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (await output).Replace("\0", "");
    }

    // Leaving keeps the engine running: dockyard-host stops at logoff, and the next one attaches to it.
    public void Dispose()
    {
        Release();
        StopDomains();
        keepAlive?.Dispose();
    }
}
