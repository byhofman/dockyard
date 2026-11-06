using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Hosts;

[SupportedOSPlatform("windows")]
sealed partial class WindowsHost(ICommandRunner runner) : IEngineHost
{
    public const string Linux = "linux";
    public const string Windows = "win";

    readonly WslDistro distro = new(runner);
    Process? keepAlive;

    public IReadOnlyList<EngineInfo> Engines { get; } =
    [
        new(Linux, "Linux", "Linux containers", $"WSL distro '{WslDistro.DefaultName}'", "linux", Paths.LinuxHost, nameof(Component.LinuxEngine)),
        new(Windows, "Windows", "Windows containers", "ltsc2025 · process isolation", "win", Paths.WindowsHost, nameof(Component.Engine)),
    ];

    public IReadOnlyList<ComponentInfo> Components { get; } =
    [
        new(nameof(Component.Cli), Updates.Title(Component.Cli), RequiredBecause: "both engines, Compose and Buildx are used through it.", RequiredFlag: "cli"),
        new(nameof(Component.Engine), Updates.Title(Component.Engine), Flag: "windows-engine", Note: "Windows containers. Its images stay when you remove it. One engine has to stay.", IsEngine: true),
        new(nameof(Component.LinuxEngine), Updates.Title(Component.LinuxEngine), CheckNeedsEngine: Linux, Flag: "linux-engine", Note: "Linux containers. Its images stay when you remove it. One engine has to stay.", IsEngine: true),
        new(nameof(Component.Compose), Updates.Title(Component.Compose), Flag: "compose", Note: "Needs the Docker CLI. The dashboard's Compose actions use it."),
        new(nameof(Component.Buildx), Updates.Title(Component.Buildx), Flag: "buildx", Note: "Needs the Docker CLI. docker build uses it."),
        new(nameof(Component.App), Updates.Title(Component.App), RequiredBecause: "it is what runs everything here. Uninstall removes it."),
        new(nameof(Component.Dashboard), Updates.Title(Component.Dashboard), Flag: "dashboard", Note: "Optional. Opens from the tray."),
    ];

    public bool IsInstalled => File.Exists(Paths.AppExe);

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

        var machine = parsed.Where(c => c.Component is Component.Engine or Component.Cli or Component.Compose or Component.Buildx).ToList();
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
                if (installed)
                {
                    await UserSetup.EnableLinuxEngine(log);
                    return false;
                }
                await StopLinux();
                await UserSetup.DisableLinuxEngine(log, removeData);
                return false;
            case nameof(Component.Engine) when !installed:
                await UserSetup.RemoveContext("win", "linux", log);
                break;
        }
        var change = installed ? "add" : "remove";
        if (Elevation.IsElevated)
        {
            var (sid, name) = Elevation.CurrentUser;
            await MachineSetup.SetComponent(log, Enum.Parse<Component>(component), installed, sid, name);
        }
        else await Elevation.Run(log, "--machine-component", component, change);
        if (component == nameof(Component.Engine) && installed) await UserSetup.AddContext("win", Paths.WindowsHost);
        return false;
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

    public void ReleaseResources()
    {
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
            await Updates.DownloadFile(release.DownloadUrl!, tgz);
            foreach (var (repo, pattern, file) in new[]
            {
                ("docker/compose", Updates.LinuxComposeAsset, "docker-compose"),
                ("docker/buildx", Updates.LinuxBuildxAsset, "docker-buildx"),
            })
            {
                var plugin = await Updates.LatestGitHub(repo, pattern);
                await Updates.DownloadFile(plugin.DownloadUrl!, Path.Combine(plugins, file));
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

    static bool IsLinuxRunning()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", Paths.LinuxPort).Wait(300) && client.Connected;
        }
        catch { return false; }
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
        if (keepAlive is null || keepAlive.HasExited)
        {
            keepAlive?.Dispose();
            await UserSetup.ApplyGuest(distro, _ => { });
            keepAlive = runner.Spawn("wsl.exe", distro.KeepAliveArgs());
        }
        var proc = keepAlive;
        await Wait.For(IsLinuxRunning, TimeSpan.FromSeconds(90), "Linux engine", failed: () => proc.HasExited);
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
