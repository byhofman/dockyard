using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The Mac's side of IEngineHost. The VM, the components, the install and the trust store each have a class of their own.
[SupportedOSPlatform("macos")]
sealed class MacHost(ICommandRunner runner, Func<bool>? ping = null, Func<bool>? helperAnswers = null) : IEngineHost
{
    public const string Engine = "linux";
    readonly MacVm vm = new(runner, ping, helperAnswers);
    readonly MacComponents parts = new(runner);
    readonly MacTrust trust = new(runner);
    MacSetup Setup => new(runner, parts, vm);

    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new(Engine, "Linux", "Linux containers", "Dockyard VM (Virtualization.framework)", MacLayout.Context, "unix://" + MacLayout.DockerSocket)];

    public IReadOnlyList<ComponentInfo> Components => Catalog;

    public static IReadOnlyList<ComponentInfo> Catalog { get; } =
    [
        new("LinuxEngine", "Docker Engine (Linux VM)", RequiredBecause: "it runs your containers, and it is the only engine here. Uninstall removes it.", RequiredFlag: "linux-engine"),
        new("Cli", "Docker CLI", RequiredBecause: "the engine, Compose and Buildx are used through it.", RequiredFlag: "cli"),
        new("Compose", "Docker Compose", Flag: "compose", Note: "Needs the Docker CLI. The dashboard's Compose actions use it."),
        new("Buildx", "Docker Buildx", Flag: "buildx", Note: "Needs the Docker CLI. docker build uses it."),
        Kind.Component,
        new("App", "Dockyard", RequiredBecause: "it is what runs everything here. Uninstall removes it."),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional. Opens from the menu bar."),
    ];

    public bool IsInstalled => File.Exists(MacLayout.VmConfig) && File.Exists(MacLayout.DockerCli);

    public Task<bool> IsRunning(string engine) => vm.IsRunning();
    public Task Start(string engine) => vm.Start();
    public Task Stop(string engine) => vm.Stop();

    public string ActiveEngine() => Engine;

    public Task UseContext(string engine) => runner.Check(MacLayout.DockerCli, "context", "use", MacLayout.Context);

    public Task<Version?> Installed(string component) => parts.Installed(component);
    public Task<Release?> Latest(string component) => MacComponents.Latest(component);

    public string CaDirectory => Path.Combine(MacLayout.DataDir, "ca");
    public ICaKeyStore CaKeys => new KeychainCaKey(MacLayout.LoginKeychain);
    public Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => trust.TrustCa(log, certificateFile, silent);
    public Task UntrustCa(Action<string> log, string certificateFile) => trust.UntrustCa(log, certificateFile);

    public bool HasEngineConfiguration => true;
    public string? OwnSocket => MacLayout.DockerSocket;

    public Task ApplyEngineConfiguration(Action<string> log) => vm.ApplyEngineConfiguration(log);

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        foreach (var (component, release) in components)
        {
            switch (component)
            {
                case "Cli" when release is not null: await parts.InstallCli(log, release); break;
                case "Compose" when release is not null: await MacComponents.InstallPlugin(log, "docker-compose", release); break;
                case "Buildx" when release is not null: await MacComponents.InstallPlugin(log, "docker-buildx", release); break;
                case Kind.Id: await MacComponents.InstallKind(log); break;
                case ComponentRules.DashboardId when release is not null: await parts.AddDashboard(log, release.Tag); break;
                case "LinuxEngine": await vm.Update(log); break;
                case "App":
                    await parts.SelfUpdate(log, engineOnly: !HasDashboard, release?.Tag);
                    return true;
            }
        }
        return false;
    }

    public bool RunsFromInstallLocation => MacSetup.RunsFromInstallLocation;

    public bool HasDashboard => MacComponents.HasDashboard;

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        if (installed) await parts.AddDashboard(log);
        else await parts.RemoveDashboard(log);
        return false;
    }

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        var plugin = component switch { "Compose" => "docker-compose", "Buildx" => "docker-buildx", _ => null };
        if (component == ComponentRules.DashboardId) return await SetDashboard(log, installed);
        if (component == Kind.Id)
        {
            if (installed) await MacComponents.InstallKind(log);
            else MacComponents.RemoveKind(log);
            return false;
        }
        if (plugin is null) throw new InvalidOperationException($"{component} cannot be changed here");
        if (!installed)
        {
            MacComponents.RemovePlugin(plugin);
            log($"Removed {plugin}");
        }
        else await MacComponents.InstallPlugin(log, plugin, (await Latest(component))!);
        return false;
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        await Setup.Install(log, request, this);
        return false;
    }

    public void StartApp()
    {
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle) Shell.Spawn("/usr/bin/open", bundle, "--args", "--tray").Dispose();
        else Shell.Spawn(MacLayout.InstalledExe, "--tray").Dispose();
    }

    public string DoneMessage(bool reboot) => "Done. Dockyard runs in the menu bar.";

    public Task Uninstall(Action<string> log, bool removeData) => Setup.Uninstall(log, removeData, this);

    public void ReleaseResources() => vm.ReleaseResources();
}
