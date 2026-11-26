using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup;

// Every setting a front end shows, with the words it shows them in. What setting one does is DockyardSetup.SetSetting's.
public static class SettingDefinitions
{
    public const string ContainerDomains = "container-domains";
    public const string TrustedHttps = "trusted-https";
    public const string StartEngineAtLogon = "start-engine-at-logon";
    public const string StopOtherEngineOnSwitch = "stop-other-engine-on-switch";
    public const string KubectlShell = "kubectl-shell";
    public const string Source = "source";
    public const string EngineFilePrefix = "engine-file:";
    public const string On = "on";
    public const string Off = "off";
    public const string DockyardSource = "Dockyard";
    public const string DomainsGroup = "Domains & HTTPS";
    public const string EngineGroup = "Engine";
    public const string ShellGroup = "Shell";
    public const string ToolsGroup = "Tools";

    static readonly Lazy<IReadOnlyList<SettingDefinition>> ThisMachine = new(() => For(HostFactory.Create().Engines));

    // This computer's settings.
    public static IReadOnlyList<SettingDefinition> All => ThisMachine.Value;

    // Where Linux is the only engine, as on macOS and Linux, saying a setting is for it tells nothing.
    public static bool ShowsLinuxEngineOnly(SettingDefinition definition) => ShowsLinuxEngineOnly(definition, OperatingSystem.IsWindows());

    public static bool ShowsLinuxEngineOnly(SettingDefinition definition, bool windows) => definition.LinuxEngineOnly && windows;

    // The package manager's name, as the first choice of the source setting.
    public static string PackageManager => OperatingSystem.IsWindows() ? "winget"
        : OperatingSystem.IsMacOS() ? "Homebrew"
        : File.Exists("/etc/os-release") && OsRelease.Parse(File.ReadAllText("/etc/os-release")).PackageManager is { } family ? family
        : "apt or dnf";

    // Grouped as a front end shows them: domains and HTTPS, then the engine, then the shell; the download source sits with
    // the tools in Tools.
    internal static IReadOnlyList<SettingDefinition> For(IReadOnlyList<EngineInfo> engines)
    {
        List<SettingDefinition> all =
        [
            new(ContainerDomains, "Container domains",
                "Each running container answers at http://name.dockyard.internal, and a cluster's ingress at host.cluster.dockyard.internal, without a published port.",
                SettingKind.Toggle, null, LinuxEngineOnly: true, NeedsAdmin: false, DomainsGroup),
            new(TrustedHttps, "Trusted HTTPS for container domains",
                "Adds https to those names, with a certificate authority made on this computer and valid only for dockyard.internal. Your system asks to trust it.",
                SettingKind.Toggle, null, LinuxEngineOnly: true, NeedsAdmin: true, DomainsGroup),
            new(StartEngineAtLogon, "Start the active engine at logon",
                "When Dockyard starts at logon, it starts the engine that was in use.",
                SettingKind.Toggle, null, LinuxEngineOnly: false, NeedsAdmin: false, EngineGroup),
        ];
        if (engines.Count > 1)
            all.Add(new(StopOtherEngineOnSwitch, "Stop the other engine when switching",
                "Switching between the Linux and Windows engines stops the one you leave.",
                SettingKind.Toggle, null, LinuxEngineOnly: false, NeedsAdmin: false, EngineGroup));
        foreach (var engine in engines)
            all.Add(new(EngineFilePrefix + engine.Id, $"{engine.Name} engine settings", EngineFileDescription(engine.Id),
                SettingKind.EngineFile, null, LinuxEngineOnly: false, NeedsAdmin: engine.Id == EngineSettings.Windows, EngineGroup));
        all.Add(new(KubectlShell, "Set up kubectl in my shell", KubectlShellDescription, SettingKind.Toggle, null, LinuxEngineOnly: false, NeedsAdmin: false, ShellGroup));
        all.Add(new(Source, "Download source", "Where docker, kind, kubectl and helm come from. Changing it moves them at once.",
            SettingKind.Choice, [PackageManager, DockyardSource], LinuxEngineOnly: false, NeedsAdmin: false, ToolsGroup));
        return all;
    }

    static string KubectlShellDescription =>
        $"Adds k, completion, {ShellSetup.DoAndNowHere} to " +
        (OperatingSystem.IsWindows() ? "your PowerShell profiles and Git Bash's .bashrc"
            : OperatingSystem.IsMacOS() ? "your .zshrc" : "your .bashrc and .zshrc") +
        ". Off takes them out again.";

    static string EngineFileDescription(string engine) => engine == EngineSettings.Windows
        ? "C:\\ProgramData\\docker\\config\\daemon.json. Applying asks for administrator approval and restarts the engine if it runs."
        : "The engine's daemon.json. Dockyard adds \"dns\" and your proxy. Applying restarts the engine, which stops containers without a restart policy.";
}
