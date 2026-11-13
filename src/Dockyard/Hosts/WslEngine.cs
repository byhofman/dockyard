using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The Linux engine in the WSL distro: the process that keeps the distro up, and the pipes Windows reaches the engine by.
[SupportedOSPlatform("windows")]
sealed class WslEngine(ICommandRunner runner, WslDistro distro)
{
    Process? keepAlive;
    WslEngineRelay? relay;
    DateTime lastNatLook;

    // Windows reaches the engine only through this process's pipes, so the agent behind them is what has to answer.
    public bool IsRunning()
    {
        if (relay is not { } active) return false;
        if (active.EngineAnswers(300)) return true;
        active.Dispose();
        relay = null;
        return false;
    }

    // Only asked while the distro runs: running anything in it would otherwise start it.
    public async Task<bool> RelayToEngine()
    {
        if (relay is not null) return true;
        var running = await runner.Run("wsl.exe", "-l", "--running", "-q");
        if (!running.Output.Split('\n').Select(l => l.Trim().Trim('\0')).Contains(distro.Name, StringComparer.OrdinalIgnoreCase)) return false;
        var ip = await runner.Run("wsl.exe", "-d", distro.Name, "-u", "root", "--", "sh", "-c", "[ -d /sys/class/net/loopback0 ] && echo mirrored || ip -4 -o addr show eth0");
        if (WslEngineGate.AddressFromIp(ip.Output) is not { } address || !WslEngineRelay.Answers(address, Paths.LinuxPort, 500)) return false;
        var candidate = new WslEngineRelay(address, WslEngineGate.Token(Paths.EngineTokenFile));
        if (!candidate.Start())
        {
            candidate.Dispose();
            return false;
        }
        relay = candidate;
        return true;
    }

    bool Ready()
    {
        if (IsRunning()) return true;
        if (DateTime.UtcNow - lastNatLook < TimeSpan.FromSeconds(2)) return false;
        lastNatLook = DateTime.UtcNow;
        return RelayToEngine().GetAwaiter().GetResult();
    }

    public async Task Start()
    {
        if (keepAlive is { HasExited: false } && IsRunning()) return;
        // Started by something else, such as a tray that was replaced: a second dockyard-init would take its socket away.
        if ((keepAlive is null || keepAlive.HasExited) && await RelayToEngine()) return;
        if (keepAlive is null || keepAlive.HasExited)
        {
            keepAlive?.Dispose();
            await UserSetup.ApplyGuest(distro, _ => { });
            keepAlive = runner.Spawn("wsl.exe", distro.KeepAliveArgs());
        }
        var proc = keepAlive;
        lastNatLook = DateTime.MinValue;
        await Wait.For(Ready, TimeSpan.FromSeconds(90), "Linux engine", failed: () => proc.HasExited);
    }

    public async Task Stop()
    {
        if (keepAlive is { HasExited: false } || IsRunning()) await distro.StopEngine();
        ReleaseResources();
    }

    public void ReleaseResources()
    {
        relay?.Dispose();
        relay = null;
        if (keepAlive is { HasExited: false }) keepAlive.Kill(entireProcessTree: true);
        keepAlive?.Dispose();
        keepAlive = null;
    }

    public async Task ApplyEngineConfiguration(Action<string> log)
    {
        var running = IsRunning();
        if (running) await Stop();
        await UserSetup.WriteEngineConfig(distro, log);
        if (running) await Start();
    }

    public async Task Update(Action<string> log, Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        var plugins = Path.Combine(dir, "plugins");
        Directory.CreateDirectory(plugins);
        try
        {
            log($"Downloading Docker {release.Version} for Linux");
            var tgz = Path.Combine(dir, "docker.tgz");
            await Pinned.Download(release, tgz);
            foreach (var (plugin, file) in new[] { (Updates.LinuxCompose, "docker-compose"), (Updates.LinuxBuildx, "docker-buildx") })
            {
                await Pinned.Download(plugin, Path.Combine(plugins, file));
                log($"{file} {plugin.Tag}");
            }

            var wasRunning = IsRunning();
            if (wasRunning) await Stop();
            log(await distro.Update(tgz, plugins));
            if (wasRunning) await Start();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
