using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Dockyard.Core;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Hosts;

[SupportedOSPlatform("windows")]
sealed partial class WindowsHost(ICommandRunner runner) : IEngineHost
{
    public const string Linux = "linux";
    public const string Windows = "win";

    readonly WslDistro distro = new(runner);
    Process? keepAlive;
    WslEngineRelay? relay;
    DateTime lastNatLook;
    CancellationTokenSource? trayServices;
    DomainRelay? domainRelay;

    public IReadOnlyList<EngineInfo> Engines { get; } =
    [
        new(Linux, "Linux", "Linux containers", $"WSL distro '{WslDistro.DefaultName}'", "linux", Paths.LinuxHost, nameof(Component.LinuxEngine)),
        new(Windows, "Windows", "Windows containers", "ltsc2025 · process isolation", "win", Paths.WindowsHost, nameof(Component.Engine)),
    ];

    public IReadOnlyList<ComponentInfo> Components => Catalog;

    public static IReadOnlyList<ComponentInfo> Catalog { get; } =
    [
        new(nameof(Component.Cli), Updates.Title(Component.Cli), RequiredBecause: "both engines, Compose and Buildx are used through it.", RequiredFlag: "cli"),
        new(nameof(Component.Engine), Updates.Title(Component.Engine), Flag: "windows-engine", Note: "Windows containers. Its images stay when you remove it. One engine has to stay.", IsEngine: true),
        new(nameof(Component.LinuxEngine), Updates.Title(Component.LinuxEngine), CheckNeedsEngine: Linux, Flag: "linux-engine", Note: "Linux containers. Its images stay when you remove it. One engine has to stay.", IsEngine: true),
        new(nameof(Component.Compose), Updates.Title(Component.Compose), Flag: "compose", Note: "Needs the Docker CLI. The dashboard's Compose actions use it."),
        new(nameof(Component.Buildx), Updates.Title(Component.Buildx), Flag: "buildx", Note: "Needs the Docker CLI. docker build uses it."),
        Kind.Component with { Note = "Local Kubernetes clusters in containers. Needs the Linux engine; the dashboard's Clusters section uses it." },
        new(nameof(Component.App), Updates.Title(Component.App), RequiredBecause: "it is what runs everything here. Uninstall removes it."),
        new(nameof(Component.Dashboard), Updates.Title(Component.Dashboard), Flag: "dashboard", Note: "Optional. Opens from the tray."),
    ];

    public bool IsInstalled => File.Exists(Paths.AppExe);
    public ICaKeyStore CaKeys => new DpapiCaKey(((IEngineHost)this).CaDirectory);

    public Task<bool> IsRunning(string engine) => Task.Run(() => engine == Linux ? IsLinuxRunning() : IsWindowsRunning());

    public Task Start(string engine) => engine == Linux ? StartLinux() : StartWindows();
    public Task Stop(string engine) => engine == Linux ? StopLinux() : StopWindows();

    public string ActiveEngine()
    {
        try
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.UserProfile, ".docker", "config.json")));
            return config?["currentContext"]?.GetValue<string>() == "win" ? Windows : Linux;
        }
        catch { return Linux; }
    }

    public Task UseContext(string engine) => runner.Check(Paths.DockerExe, "context", "use", engine == Windows ? "win" : "linux");

    public Task<Version?> Installed(string component) => Updates.Installed(Enum.Parse<Component>(component));
    public Task<Release?> Latest(string component) => Updates.Latest(Enum.Parse<Component>(component));

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        var parsed = components.Select(c => (Component: Enum.Parse<Component>(c.Component), c.Release)).ToList();

        var machine = parsed.Where(c => c.Component is Component.Engine or Component.Cli or Component.Compose or Component.Buildx or Component.Kind).ToList();
        if (machine.Count > 0)
            await Elevation.Run(log, "--machine-update", string.Join(',', machine.Select(m => m.Component)));

        if (parsed.FirstOrDefault(c => c.Component == Component.LinuxEngine) is { Release: { } linux })
            await UpdateLinuxEngine(log, linux);

        if (parsed.FirstOrDefault(c => c.Component == Component.Dashboard) is { Release: { } dashboard })
            await UpdateDashboard(log, dashboard);

        if (parsed.FirstOrDefault(c => c.Component == Component.App) is { Release: { } app })
        {
            log($"Downloading Dockyard {app.Tag}");
            var exe = await Updates.DownloadApp(app);
            Elevation.StartDetached("--machine-update", nameof(Component.App), "--source", exe);
            log("Restarting Dockyard");
            return true;
        }
        return false;
    }

    async Task UpdateDashboard(Action<string> log, Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            log($"Downloading the dashboard {release.Tag}");
            await Updates.DownloadReleaseAsset(Paths.DashboardZipName, dir, tag: release.Tag);
            if (Elevation.IsElevated) await MachineSetup.UpdateDashboard(log, dir, Environment.ProcessId);
            else await Elevation.Run(log, "--machine-dashboard", "update", "--source-dir", dir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        switch (component)
        {
            case nameof(Component.Dashboard):
                return await SetDashboard(log, installed);
            case nameof(Component.LinuxEngine):
                // First: a declined approval then leaves everything as it was.
                await MachineComponent(log, component, installed);
                if (installed)
                {
                    await UserSetup.EnableLinuxEngine(log);
                    if (trayServices is not null) StartDomainRelay();
                    return false;
                }
                await StopLinux();
                await UserSetup.DisableLinuxEngine(log, removeData);
                domainRelay?.Dispose();
                domainRelay = null;
                return false;
            case nameof(Component.Engine) when !installed:
                await UserSetup.RemoveContext("win", "linux", log);
                break;
        }
        await MachineComponent(log, component, installed);
        if (component == nameof(Component.Engine) && installed) await UserSetup.AddContext("win", Paths.WindowsHost);
        return false;
    }

    static async Task MachineComponent(Action<string> log, string component, bool installed)
    {
        if (Elevation.IsElevated)
        {
            var (sid, name) = Elevation.CurrentUser;
            await MachineSetup.SetComponent(log, Enum.Parse<Component>(component), installed, sid, name);
        }
        else await Elevation.Run(log, "--machine-component", component, installed ? "add" : "remove");
    }

    // The machine's store: the current user's would show its own dialog from an elevated process nobody is watching, and
    // the wizard's administrator approval is the prompt that explains itself.
    public async Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent)
    {
        try
        {
            var pem = await File.ReadAllTextAsync(certificateFile);
            if (Elevation.IsElevated) MachineSetup.TrustCa(log, pem);
            // The certificate itself and not its path: approval can come from another administrator's account, which
            // cannot read this user's owner-only folder.
            else await Elevation.Run(log, "--machine-trust-ca", MachineSetup.EncodeCertificate(pem));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    public async Task UntrustCa(Action<string> log, string certificateFile)
    {
        if (Elevation.IsElevated) MachineSetup.UntrustCa(log);
        else if (File.Exists(certificateFile)) await Elevation.Run(log, "--machine-untrust-ca", "all");
    }

    static bool LinuxEngineWanted => !Settings.Load().RemovedComponents.Contains(nameof(Component.LinuxEngine));
    public bool HasEngineConfiguration => LinuxEngineWanted;

    public async Task ApplyEngineConfiguration(Action<string> log)
    {
        var running = IsLinuxRunning();
        if (running) await StopLinux();
        await UserSetup.WriteEngineConfig(distro, log);
        if (running) await StartLinux();
    }

    public bool RunsFromInstallLocation => Paths.IsInstalledCopy;

    public bool HasDashboard => File.Exists(Paths.DashboardExe) || File.Exists(Paths.LegacyDashboardExe);

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        if (Elevation.IsElevated) await MachineSetup.SetDashboard(log, installed, Environment.ProcessId);
        else await Elevation.Run(log, "--machine-dashboard", installed ? "add" : "remove");
        return false;
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        log("Administrator step");
        var reboot = await MachineInstall(log, request.Dashboard, request.Without);
        log("User step");
        var withLinux = !request.Without.Contains(nameof(Component.LinuxEngine));
        var hadDistro = withLinux && await distro.Exists();
        try
        {
            await UserSetup.Install(log, request.Image, request.Without);
        }
        catch (Exception ex) when (reboot)
        {
            // WSL does not work until the restart its install asked for, and the rest of setup needs WSL.
            log($"The Linux engine has to wait for the restart ({ex.Message})");
            UserSetup.ResumeAfterReboot(request.Silent, log);
            return true;
        }
        if (hadDistro) await UpdateLinuxEngineIfNewer(log);
        if (request.TrustHttps) await Https.Enable(this, log, request.Silent);
        return reboot;
    }

    public async Task Resume(Action<string> log)
    {
        log("Finishing the setup after the restart");
        await UserSetup.Install(log);
    }

    public void StartApp() => Shell.StartUnelevated(Paths.AppExe);

    public string DoneMessage(bool reboot) => reboot
        ? "Restart Windows to finish. Dockyard carries on by itself after the restart."
        : "Done. Dockyard runs in the tray.";

    public void Reboot() => Shell.Spawn("shutdown.exe", "/r", "/t", "5", "/d", "p:4:1").Dispose();

    public async Task Uninstall(Action<string> log, bool removeData)
    {
        ReleaseResources();
        // Before the user's folder with the certificate goes; the machine step also removes it by name, to leave none behind.
        await LocalCa.Remove(((IEngineHost)this).CaDirectory, CaKeys);
        await UserSetup.Uninstall(log, removeData);
        if (Elevation.IsElevated) await MachineSetup.Uninstall(log, Environment.ProcessId);
        else await Elevation.Run(log, "--machine-uninstall");
    }

    static async Task<bool> MachineInstall(Action<string> log, bool dashboard, IReadOnlyCollection<string> without)
    {
        if (!Elevation.IsElevated)
        {
            string[] extra = [.. dashboard ? Array.Empty<string>() : ["--engine-only"], .. without.Count > 0 ? new[] { "--without", string.Join(',', without) } : []];
            return await Elevation.Run(log, "--machine-install", extra);
        }
        var (sid, name) = Elevation.CurrentUser;
        return await MachineSetup.Install(log, sid, name, Environment.ProcessId, engineOnly: !dashboard, without);
    }

    async Task UpdateLinuxEngineIfNewer(Action<string> log)
    {
        try
        {
            if (await Latest(nameof(Component.LinuxEngine)) is { } latest && await Installed(nameof(Component.LinuxEngine)) is { } installed && latest.Version > installed)
                await UpdateLinuxEngine(log, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            log($"The Linux engine was left as it is ({ex.Message}); Updates in the tray window can try again");
        }
    }

    void StartDomainRelay()
    {
        if (domainRelay is not null) return;
        domainRelay = new DomainRelay(_ => { });
        domainRelay.Start();
    }

    public void StartTrayServices()
    {
        if (trayServices is not null) return;
        trayServices = new CancellationTokenSource();
        _ = FollowPublishedPorts(trayServices.Token);
        // An engine this tray did not start, for example before the tray was replaced, gets its pipes again.
        if (LinuxEngineWanted) _ = Task.Run(RelayToEngine);
        if (LinuxEngineWanted) StartDomainRelay();
    }

    // The tray does not have to be what started the engine, and the engine comes and goes without telling it.
    async Task FollowPublishedPorts(CancellationToken ct)
    {
        using var forwarder = new LoopbackForwarder(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var docker = new DockerClient(DockerEndpoint.Parse(Paths.LinuxHost));
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                async Task Refresh() => forwarder.Apply(PublishedPorts.From(await docker.ListContainersAsync(all: false, ct))
                    .Where(p => p.Protocol == "tcp").Select(p => p.Port));
                await Refresh();
                await foreach (var e in docker.EventsAsync(["container"], since, ct))
                    if (e.Action is "start" or "die" or "destroy") await Refresh();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                forwarder.Apply([]);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    public void ReleaseResources()
    {
        relay?.Dispose();
        relay = null;
        if (keepAlive is { HasExited: false }) keepAlive.Kill(entireProcessTree: true);
        keepAlive?.Dispose();
        keepAlive = null;
    }

    async Task UpdateLinuxEngine(Action<string> log, Release release)
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

            var wasRunning = IsLinuxRunning();
            if (wasRunning) await StopLinux();
            log(await distro.Update(tgz, plugins));
            if (wasRunning) await StartLinux();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // Windows reaches the engine only through this process's pipes, so the agent behind them is what has to answer.
    bool IsLinuxRunning()
    {
        if (relay is not { } active) return false;
        if (active.EngineAnswers(300)) return true;
        active.Dispose();
        relay = null;
        return false;
    }

    // Only asked while the distro runs: running anything in it would otherwise start it.
    async Task<bool> RelayToEngine()
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

    bool LinuxReady()
    {
        if (IsLinuxRunning()) return true;
        if (DateTime.UtcNow - lastNatLook < TimeSpan.FromSeconds(2)) return false;
        lastNatLook = DateTime.UtcNow;
        return RelayToEngine().GetAwaiter().GetResult();
    }

    static bool IsWindowsRunning()
    {
        try
        {
            using var sc = new ServiceController(Paths.ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    async Task StartLinux()
    {
        if (keepAlive is { HasExited: false } && IsLinuxRunning()) return;
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
        await Wait.For(LinuxReady, TimeSpan.FromSeconds(90), "Linux engine", failed: () => proc.HasExited);
    }

    async Task StopLinux()
    {
        if (keepAlive is { HasExited: false } || IsLinuxRunning()) await distro.StopEngine();
        ReleaseResources();
    }

    static async Task StartWindows()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)));
        }
        await Wait.For(() => PipeExists(Paths.WindowsPipeName), TimeSpan.FromSeconds(30), "Windows engine");
    }

    static async Task StopWindows()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)));
    }

    static bool PipeExists(string name)
    {
        if (WaitNamedPipe($@"\\.\pipe\{name}", 1)) return true;
        const int ErrorSemTimeout = 121;
        return Marshal.GetLastPInvokeError() == ErrorSemTimeout;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, int timeout);
}
