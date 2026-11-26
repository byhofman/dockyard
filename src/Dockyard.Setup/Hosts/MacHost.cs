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
    ToolComponents Tools => new(new Homebrew(runner), new MacDockyardSource(runner, parts), () => Settings.Load().Source, new ToolLedger(Path.Combine(Paths.SettingsDir, "package-tools.json")));
    MacSetup Setup => new(runner, parts, vm, Tools);

    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new(Engine, "Linux", "Linux containers", "Dockyard VM (Virtualization.framework)", MacLayout.Context, "unix://" + MacLayout.DockerSocket)];

    public IReadOnlyList<ComponentInfo> Components => Catalog;

    public static IReadOnlyList<ComponentInfo> Catalog { get; } =
    [
        new(nameof(Component.DockyardCli), Updates.Title(Component.DockyardCli), RequiredBecause: "it installs and updates everything else here. Uninstall removes it.", RequiredFlag: "dockyard-cli"),
        new("LinuxEngine", "Docker Engine (Linux VM)", RequiredBecause: "it runs your containers, and it is the only engine here. Uninstall removes it.", RequiredFlag: "linux-engine"),
        new(nameof(Component.DockerCli), "Docker CLI", RequiredBecause: "the engine, Compose and Buildx are used through it.", RequiredFlag: "docker-cli"),
        new("Compose", "Docker Compose", Flag: "compose", Note: "Needs the Docker CLI. The dashboard's Compose actions use it."),
        new("Buildx", "Docker Buildx", Flag: "buildx", Note: "Needs the Docker CLI. docker build uses it."),
        Core.Tools.Kind.Component,
        Core.Tools.Kubectl.Component,
        Core.Tools.Helm.Component,
        new(ComponentRules.TrayId, "Dockyard menu bar app", Flag: "tray", Note: "Optional. Keeps the engine in the menu bar and starts at login."),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional. Opens from the menu bar."),
    ];

    public bool IsInstalled => File.Exists(MacLayout.VmConfig) && File.Exists(MacLayout.Docker);

    public Task<bool> IsRunning(string engine) => vm.IsRunning();
    public Task Start(string engine) => vm.Start();
    public Task Stop(string engine) => vm.Stop();

    public string ActiveEngine() => Engine;

    public Task UseContext(string engine) => runner.Check(MacLayout.Docker, "context", "use", MacLayout.Context);

    public Task<Version?> Installed(string component) => Core.Tools.Find(component) is { } tool ? Tools.Installed(tool) : parts.Installed(component);
    public Task<Release?> Latest(string component) => Core.Tools.Find(component) is { } tool ? Tools.Latest(tool) : MacComponents.Latest(component);
    public Task<string?> Source(string component) => Core.Tools.Find(component) is { } tool ? Tools.Source(tool) : Task.FromResult<string?>("Dockyard");

    public string CaDirectory => Path.Combine(MacLayout.DataDir, "ca");
    public ICaKeyStore CaKeys => new KeychainCaKey(MacLayout.LoginKeychain);
    public Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => trust.TrustCa(log, certificateFile, silent);
    public Task UntrustCa(Action<string> log, string certificateFile, bool silent) => trust.UntrustCa(log, certificateFile, silent);

    public bool HasEngineConfiguration => true;
    public string? OwnSocket => MacLayout.DockerSocket;

    public Task ApplyEngineConfiguration(Action<string> log) => vm.ApplyEngineConfiguration(log);

    public Task<string?> ReadEngineSettings(string engine) => Task.FromResult<string?>(RenderedEngineSettings.Read());
    public Task ApplyEngineSettings(Action<string> log, string engine, string json) => RenderedEngineSettings.Apply(log, json, vm.ApplyEngineConfiguration);

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        var tools = Tools;
        foreach (var (component, release) in components)
        {
            if (Core.Tools.Find(component) is { } tool)
            {
                await tools.Install(log, tool);
                continue;
            }
            switch (component)
            {
                case ComponentRules.DashboardId when release is not null: await parts.AddDashboard(log, release.Tag); break;
                case "LinuxEngine": await vm.Update(log); break;
                case nameof(Component.DockyardCli):
                    await parts.SelfUpdate(log, engineOnly: !HasDashboard, release?.Tag);
                    return true;
            }
        }
        await MacSetup.FindBrewPlugins(log, tools);
        return false;
    }

    public bool RunsFromInstallLocation => MacSetup.RunsFromInstallLocation;

    public string SelfComponent => nameof(Component.DockyardCli);

    public bool HasDashboard => MacComponents.HasDashboard;

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        if (installed) await parts.AddDashboard(log);
        else await parts.RemoveDashboard(log);
        return false;
    }

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        if (component == ComponentRules.DashboardId) return await SetDashboard(log, installed);
        if (component == ComponentRules.TrayId)
        {
            if (installed) await MacSetup.AddLoginItem(log);
            else MacSetup.RemoveLoginItem(log);
            return false;
        }
        if (Core.Tools.Find(component) is not { } tool) throw new InvalidOperationException($"{component} cannot be changed here");
        var tools = Tools;
        if (!installed) await tools.Remove(log, tool);
        else if (await tools.Install(log, tool)) await MacSetup.FindBrewPlugins(log, tools);
        return false;
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        await Setup.Install(log, request, this);
        return false;
    }

    public void StartApp()
    {
        if (!File.Exists(MacLayout.LaunchAgent)) return;
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle) Shell.Spawn("/usr/bin/open", bundle, "--args", "--tray").Dispose();
        else Shell.Spawn(MacLayout.InstalledExe, "--tray").Dispose();
    }

    public string DoneMessage(bool reboot) => "Done. Dockyard runs in the menu bar.";

    public Task Uninstall(Action<string> log, bool removeData, bool silent) => Setup.Uninstall(log, removeData, silent, this);

    public void ReleaseResources() => vm.ReleaseResources();
}
