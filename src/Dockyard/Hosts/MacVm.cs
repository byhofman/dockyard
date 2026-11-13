using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The Linux VM that runs the engine: its helper process, its images and the daemon.json it boots with.
[SupportedOSPlatform("macos")]
sealed class MacVm(ICommandRunner runner, Func<bool>? ping = null, Func<bool>? helperAnswers = null)
{
    static readonly TimeSpan HelperPatience = TimeSpan.FromSeconds(60);
    readonly Func<bool> ping = ping ?? Ping;
    readonly Func<bool> helperAnswers = helperAnswers ?? HelperAnswers;
    Process? vm;

    public Task<bool> IsRunning() => Task.Run(ping);

    public async Task Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            // The engine can outlive the tray that started it; a second helper would only truncate its console log and fail.
            if (await Task.Run(ping)) return;
            // A helper that answers while its engine does not is starting or stopping, and holds the disk images either way.
            var until = DateTime.UtcNow + HelperPatience;
            while (await Task.Run(helperAnswers) && DateTime.UtcNow < until)
            {
                if (await Task.Run(ping)) return;
                await Task.Delay(200);
            }
            if (vm is null || vm.HasExited)
            {
                vm?.Dispose();
                if (!File.Exists(MacLayout.VmConfig)) throw new InvalidOperationException("Dockyard is not set up yet; run the installer first");
                // Keeps vm.json on this app's kernel after the app moved, and the root image's modules matching that kernel after an update.
                await Prepare(_ => { });
                await WriteEngineConfig();
                vm = runner.Spawn(MacLayout.Helper, "run", "--config", MacLayout.VmConfig);
            }
            var process = vm;
            try
            {
                await Wait.For(ping, HelperPatience, "Linux engine", failed: () => process.HasExited);
                return;
            }
            // A stop from elsewhere can arrive while this start waits, which ends the helper it was waiting for.
            catch (InvalidOperationException) when (attempt == 0) { }
        }
    }

    public async Task Stop()
    {
        var asked = File.Exists(MacLayout.ControlSocket) && await SendControl("stop");
        // A helper this process did not start still holds the disk images until it exits, and a new VM cannot attach them before that.
        if (asked && vm is null)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (File.Exists(MacLayout.ControlSocket) && DateTime.UtcNow < until) await Task.Delay(200);
        }
        if (vm is { } process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); }
            process.Dispose();
            vm = null;
        }
    }

    public async Task ApplyEngineConfiguration(Action<string> log)
    {
        await WriteEngineConfig(log);
        if (ping()) { await Stop(); await Start(); }
    }

    public async Task Update(Action<string> log)
    {
        var running = ping();
        if (running) await Stop();
        await Prepare(log);
        if (running) await Start();
    }

    // A VM running on the previous image has to stop for the new one; with the same image it can keep running.
    public async Task PrepareForInstall(Action<string> log)
    {
        var bundled = Path.Combine(MacLayout.Assets, "dockyard-release");
        var changed = File.Exists(MacLayout.RootRelease) && File.Exists(bundled) && await File.ReadAllTextAsync(MacLayout.RootRelease) != await File.ReadAllTextAsync(bundled);
        var restart = changed && ping();
        if (restart) await Stop();
        await Prepare(log);
        if (restart) await Start();
    }

    public void ReleaseResources()
    {
        if (vm is { HasExited: false })
        {
            SendControl("stop").Wait(TimeSpan.FromSeconds(2));
            if (!vm.WaitForExit(TimeSpan.FromSeconds(25))) vm.Kill();
        }
        vm?.Dispose();
        vm = null;
    }

    async Task<ProxySettings?> HostProxy()
    {
        if (HostProxySettings.FromEnvironment(Environment.GetEnvironmentVariable) is { } fromEnvironment) return fromEnvironment;
        var r = await runner.Run("/usr/sbin/scutil", "--proxy");
        return r.ExitCode == 0 ? HostProxySettings.FromScutil(r.Output) : null;
    }

    // Before every boot, because what the host uses changes with the network it is on.
    public async Task WriteEngineConfig(Action<string>? log = null)
    {
        var proxy = await HostProxy();
        Directory.CreateDirectory(Path.GetDirectoryName(MacLayout.EngineConfigFile)!);
        await File.WriteAllTextAsync(MacLayout.EngineConfigFile, EngineConfig.Render(proxy, Settings.Load()));
        await EngineConfig.ApplyClientProxy("unix://" + MacLayout.DockerSocket, proxy);
        if (proxy is { IsEmpty: false }) log?.Invoke("The engine and containers use this Mac's proxy");
    }

    async Task Prepare(Action<string> log)
    {
        var r = await runner.Run(MacLayout.Helper, "prepare", "--assets", MacLayout.Assets, "--config", MacLayout.VmConfig);
        foreach (var line in (r.Output + r.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries)) log("  " + line.Trim());
        if (r.ExitCode != 0) throw new InvalidOperationException($"Preparing the VM failed ({r.ExitCode})");
    }

    static async Task<bool> SendControl(string command)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacLayout.ControlSocket));
            await socket.SendAsync(Encoding.ASCII.GetBytes(command + "\n"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    static bool HelperAnswers()
    {
        if (!File.Exists(MacLayout.ControlSocket)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(MacLayout.ControlSocket));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    static bool Ping()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 1000;
            socket.Connect(new UnixDomainSocketEndPoint(MacLayout.DockerSocket));
            socket.Send("GET /_ping HTTP/1.0\r\nHost: docker\r\n\r\n"u8);
            var buffer = new byte[256];
            var n = socket.Receive(buffer);
            return Encoding.ASCII.GetString(buffer, 0, n).StartsWith("HTTP/1.0 200", StringComparison.Ordinal)
                || Encoding.ASCII.GetString(buffer, 0, n).StartsWith("HTTP/1.1 200", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
