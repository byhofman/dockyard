using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Windows' side of IEngineHost. The WSL engine, the Windows engine's service, the tray's services and the elevated setup
// steps each have a class of their own.
[SupportedOSPlatform("windows")]
sealed class WindowsHost : IEngineHost
{
    public const string Linux = "linux";
    public const string Windows = "win";

    readonly ICommandRunner runner;
    readonly WslDistro distro;
    readonly WslEngine wsl;
    readonly WindowsTrayServices tray = new();

    public WindowsHost(ICommandRunner runner)
    {
        this.runner = runner;
        distro = new WslDistro(runner);
        wsl = new WslEngine(runner, distro);
    }

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

    public Task<bool> IsRunning(string engine) => Task.Run(() => engine == Linux ? wsl.IsRunning() : WindowsServiceEngine.IsRunning());

    public Task Start(string engine) => engine == Linux ? wsl.Start() : WindowsServiceEngine.Start();
    public Task Stop(string engine) => engine == Linux ? wsl.Stop() : WindowsServiceEngine.Stop();

    public string ActiveEngine()
    {
        try
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.UserProfile, ".docker", "config.json")));
            return config?["currentContext"]?.GetValue<string>() == "win" ? Windows : Linux;
        }
        // No config, or one docker itself could not read either: docker then uses its default, which setup makes linux.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException) { return Linux; }
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
            await wsl.Update(log, linux);

        if (parsed.FirstOrDefault(c => c.Component == Component.Dashboard) is { Release: { } dashboard })
            await WindowsSetup.UpdateDashboard(log, dashboard);

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

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        switch (component)
        {
            case nameof(Component.Dashboard):
                return await SetDashboard(log, installed);
            case nameof(Component.LinuxEngine):
                // First: a declined approval then leaves everything as it was.
                await WindowsSetup.MachineComponent(log, component, installed);
                if (installed)
                {
                    await UserSetup.EnableLinuxEngine(log);
                    if (tray.Started) tray.StartDomainRelay();
                    return false;
                }
                await wsl.Stop();
                await UserSetup.DisableLinuxEngine(log, removeData);
                tray.StopDomainRelay();
                return false;
            case nameof(Component.Engine) when !installed:
                await UserSetup.RemoveContext("win", "linux", log);
                break;
        }
        await WindowsSetup.MachineComponent(log, component, installed);
        if (component == nameof(Component.Engine) && installed) await UserSetup.AddContext("win", Paths.WindowsHost);
        return false;
    }

    public Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => WindowsSetup.TrustCa(log, certificateFile);
    public Task UntrustCa(Action<string> log, string certificateFile) => WindowsSetup.UntrustCa(log, certificateFile);

    static bool LinuxEngineWanted => !Settings.Load().RemovedComponents.Contains(nameof(Component.LinuxEngine));
    public bool HasEngineConfiguration => LinuxEngineWanted;

    public Task ApplyEngineConfiguration(Action<string> log) => wsl.ApplyEngineConfiguration(log);

    public bool RunsFromInstallLocation => Paths.IsInstalledCopy;

    public bool HasDashboard => File.Exists(Paths.DashboardExe) || File.Exists(Paths.LegacyDashboardExe);

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        await WindowsSetup.SetDashboard(log, installed);
        return false;
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        log("Administrator step");
        var reboot = await WindowsSetup.MachineInstall(log, request.Dashboard, request.Without);
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
        await WindowsSetup.MachineUninstall(log);
    }

    async Task UpdateLinuxEngineIfNewer(Action<string> log)
    {
        try
        {
            if (await Latest(nameof(Component.LinuxEngine)) is { } latest && await Installed(nameof(Component.LinuxEngine)) is { } installed && latest.Version > installed)
                await wsl.Update(log, latest);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            log($"The Linux engine was left as it is ({ex.Message}); Updates in the tray window can try again");
        }
    }

    public void StartTrayServices() => tray.Start(LinuxEngineWanted, wsl.RelayToEngine);

    public void ReleaseResources() => wsl.ReleaseResources();
}
