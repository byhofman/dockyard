using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// A Linux host's side of IEngineHost: the system's dockerd, started on demand. Getting root, the components, the install
// and the trust store each have a class of their own.
[SupportedOSPlatform("linux")]
sealed class LinuxHost : IEngineHost
{
    public const string Engine = "linux";
    const string PolkitRule = "/etc/polkit-1/rules.d/50-dockyard.rules";

    readonly ICommandRunner runner;
    readonly LinuxComponents components;
    readonly LinuxSetup setup;
    readonly LinuxTrust trust;

    public LinuxHost(ICommandRunner runner)
    {
        this.runner = runner;
        var root = new LinuxRoot(runner);
        components = new LinuxComponents(runner, root);
        setup = new LinuxSetup(root, components);
        trust = new LinuxTrust(root);
    }

    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new(Engine, "Linux", "Docker Engine", "system dockerd, started on demand", "default", "unix:///var/run/docker.sock")];

    public IReadOnlyList<ComponentInfo> Components => Catalog;

    public static IReadOnlyList<ComponentInfo> Catalog { get; } =
    [
        new("Engine", "Docker Engine & CLI", RequiredBecause: "it is what Dockyard starts and stops. It stays yours to remove with your package manager, also after uninstalling Dockyard.", RequiredFlag: "linux-engine"),
        new("Compose", "Docker Compose", Flag: "compose", Note: "Needs Docker. The dashboard's Compose actions use it."),
        new("Buildx", "Docker Buildx", Flag: "buildx", Note: "Needs Docker. docker build uses it."),
        Core.Kind.Component,
        new("App", "Dockyard", RequiredBecause: "it is what runs everything here. Uninstall removes it."),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional. Its package is versioned with Dockyard, so updating it updates both."),
    ];

    // The polkit rules folder is closed to normal users, so setup leaves a marker they can see.
    public bool IsInstalled => File.Exists("/etc/dockyard/installed") || File.Exists(PolkitRule);

    public async Task<bool> IsRunning(string engine) =>
        (await runner.Run("systemctl", "is-active", "--quiet", "docker.service")).ExitCode == 0;

    public async Task Start(string engine)
    {
        await runner.Check("systemctl", "start", "docker.socket", "docker.service");
        await Wait.For(() => File.Exists("/var/run/docker.sock"), TimeSpan.FromSeconds(30), "Docker engine");
    }

    // The socket is stopped too: otherwise any client (the dashboard reconnecting) would start the engine again right away.
    public Task Stop(string engine) => runner.Check("systemctl", "stop", "docker.socket", "docker.service", "containerd.service");

    public string ActiveEngine() => Engine;

    public Task UseContext(string engine) => runner.Check("docker", "context", "use", "default");

    public Task<Version?> Installed(string component) => components.Installed(component);
    public Task<Release?> Latest(string component) => LinuxComponents.Latest(component);

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        if (component == ComponentRules.DashboardId) return await SetDashboard(log, installed);
        if (component == Core.Kind.Id)
        {
            if (installed) await components.InstallKind(log);
            else await components.RemoveKind(log);
            return false;
        }
        if (component is not ("Compose" or "Buildx")) throw new InvalidOperationException($"{component} cannot be changed here");
        await components.ChangePlugin(log, component, installed);
        return false;
    }

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        if (components.Any(c => c.Component is "Engine" or "Compose" or "Buildx")) await this.components.UpdateDockerPackages(log);
        if (components.Any(c => c.Component == Core.Kind.Id)) await this.components.InstallKind(log);
        if (components.FirstOrDefault(c => c.Component is "App" or ComponentRules.DashboardId) is { Release: { } app })
        {
            await this.components.StopDashboard();
            await this.components.InstallApp(log, HasDashboard, app.Tag);
            // The tray still holds the single-instance lock, so the new one starts just after this process exits.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", $"sleep 2; exec \"{LinuxLayout.InstalledExe}\" --tray"]) { UseShellExecute = false })?.Dispose();
            return true;
        }
        return false;
    }

    public bool RunsFromInstallLocation => LinuxComponents.RunsFromInstallLocation;

    public bool HasDashboard => LinuxComponents.HasDashboard;

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        await components.SetDashboard(log, installed);
        return false;
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        await setup.Install(log, request, this);
        return false;
    }

    public void StartApp()
    {
        if (!LinuxLayout.HasDisplay(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        Shell.Spawn(LinuxLayout.InstalledExe, "--tray").Dispose();
    }

    public string DoneMessage(bool reboot) => "Done. Dockyard and the docker command are ready.";

    public Task Uninstall(Action<string> log, bool removeData) => setup.Uninstall(log, removeData, this);

    public void ReleaseResources() { }

    public Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => trust.TrustCa(log, certificateFile);
    public Task UntrustCa(Action<string> log, string certificateFile) => trust.UntrustCa(log, certificateFile);
}
