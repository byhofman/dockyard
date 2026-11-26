using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Shared;

namespace Dockyard.Hosts;

// The Linux engine in the WSL distro. Setup puts it in place; dockyard-host keeps it running and carries Windows'
// traffic to it, so the engine stays up and reachable whether the tray or the dashboard runs or not.
[SupportedOSPlatform("windows")]
sealed class WslEngine(WslDistro distro)
{
    static readonly TimeSpan Quick = TimeSpan.FromSeconds(5);

    public bool IsRunning() => HostPipe.Ask("status", Quick).GetAwaiter().GetResult() == HostPipe.Running;

    // Only dockyard-host's pipes reach the engine; an engine that already runs gets them without being started again.
    public async Task<bool> RelayToEngine() => await EnsureHost() && await HostPipe.Ask("attach", TimeSpan.FromSeconds(30)) == HostPipe.Running;

    public async Task Start()
    {
        if (await RelayToEngine()) return;
        await UserSetup.ApplyGuest(distro, _ => { });
        if (!await EnsureHost()) throw new InvalidOperationException($"{Paths.HostExe} is not installed or does not start; run dockyard setup again");
        var answer = await HostPipe.Ask("start", TimeSpan.FromSeconds(120));
        if (answer != HostPipe.Ok) throw new InvalidOperationException(answer is null ? "dockyard-host did not answer" : answer.StartsWith("error ", StringComparison.Ordinal) ? answer[6..] : answer);
    }

    public async Task Stop() => await HostPipe.Ask("stop", TimeSpan.FromSeconds(60));

    public void ReleaseResources() { }

    public static async Task<bool> EnsureHost()
    {
        if (HostPipe.IsListening) return true;
        if (!File.Exists(Paths.HostExe)) return false;
        Process.Start(new ProcessStartInfo(Paths.HostExe) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        for (var i = 0; i < 50 && !HostPipe.IsListening; i++) await Task.Delay(100);
        return HostPipe.IsListening;
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
