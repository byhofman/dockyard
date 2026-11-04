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
        new(Linux, "Linux", "Linux containers", $"WSL distro '{WslDistro.DefaultName}'", "linux", Paths.LinuxHost),
        new(Windows, "Windows", "Windows containers", "ltsc2025 · process isolation", "win", Paths.WindowsHost),
    ];

    public IReadOnlyList<ComponentInfo> Components { get; } =
    [
        new(nameof(Component.Engine), Updates.Title(Component.Engine)),
        new(nameof(Component.Compose), Updates.Title(Component.Compose)),
        new(nameof(Component.Buildx), Updates.Title(Component.Buildx)),
        new(nameof(Component.LinuxEngine), Updates.Title(Component.LinuxEngine), CheckNeedsEngine: Linux),
        new(nameof(Component.App), Updates.Title(Component.App)),
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

        var machine = parsed.Where(c => c.Component is Component.Engine or Component.Compose or Component.Buildx).ToList();
        if (machine.Count > 0)
            await Elevation.Run(log, "--machine-update", string.Join(',', machine.Select(m => m.Component)));

        if (parsed.FirstOrDefault(c => c.Component == Component.LinuxEngine) is { Release: { } linux })
            await UpdateLinuxEngine(log, linux);

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

    public bool HasDashboard => File.Exists(Path.Combine(AppContext.BaseDirectory, "Dockyard.Dashboard.exe"));

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        await Elevation.Run(log, "--machine-dashboard", installed ? "add" : "remove");
        return false;
    }

    public async Task<bool> Install(Action<string> log, bool engineOnly)
    {
        log("Administrator step");
        var reboot = engineOnly ? await Elevation.Run(log, "--machine-install", "--engine-only") : await Elevation.Run(log, "--machine-install");
        log("User step");
        await UserSetup.Install(log);
        return reboot;
    }

    public async Task Uninstall(Action<string> log)
    {
        ReleaseResources();
        await UserSetup.Uninstall(log);
        await Elevation.Run(log, "--machine-uninstall");
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
