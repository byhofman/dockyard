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

    internal static IReadOnlyList<SettingDefinition> For(IReadOnlyList<EngineInfo> engines)
    {
        List<SettingDefinition> all =
        [
            new(ContainerDomains, "Container domains",
                "Every running container answers at http://name.dockyard.internal without a published port, a kind cluster's ingress at host.cluster.dockyard.internal, and names a cluster publishes resolve everywhere. Served by dockyard-proxy and dockyard-etcd, which go when this is off; containers keep their DNS.",
                SettingKind.Toggle, null, LinuxEngineOnly: true, NeedsAdmin: false),
            new(TrustedHttps, "Trusted HTTPS for container domains",
                "Every running container answers at http://name.dockyard.internal without a published port. Trusted HTTPS adds a certificate authority of Dockyard's own, made on this computer and valid only for names under dockyard.internal; your system asks to trust it. Off takes it out of the trust store again.",
                SettingKind.Toggle, null, LinuxEngineOnly: true, NeedsAdmin: true),
            new(StartEngineAtLogon, "Start the active engine at logon",
                "When Dockyard starts at logon, it starts the engine that was in use.",
                SettingKind.Toggle, null, LinuxEngineOnly: false, NeedsAdmin: false),
        ];
        if (engines.Count > 1)
            all.Add(new(StopOtherEngineOnSwitch, "Stop the other engine when switching",
                "Switching between the Linux and Windows engines stops the one you leave.",
                SettingKind.Toggle, null, LinuxEngineOnly: false, NeedsAdmin: false));
        all.Add(new(KubectlShell, "Set up kubectl in my shell", KubectlShellDescription, SettingKind.Toggle, null, LinuxEngineOnly: false, NeedsAdmin: false));
        all.Add(new(Source, "Download source", "Where docker, kind, kubectl and helm come from; changing it moves them at once.",
            SettingKind.Choice, [PackageManager, DockyardSource], LinuxEngineOnly: false, NeedsAdmin: false));
        foreach (var engine in engines)
            all.Add(new(EngineFilePrefix + engine.Id, $"{engine.Name} engine settings", EngineFileDescription(engine.Id),
                SettingKind.EngineFile, null, LinuxEngineOnly: false, NeedsAdmin: engine.Id == EngineSettings.Windows));
        return all;
    }

    static string KubectlShellDescription =>
        $"A marked block with k, completion, {ShellSetup.DoAndNowHere}, which Dockyard takes out again when this is off; in " +
        (OperatingSystem.IsWindows() ? "your PowerShell 7 and Windows PowerShell profiles, and Git Bash's .bashrc when you have one"
            : OperatingSystem.IsMacOS() ? "your .zshrc" : "your .bashrc, and .zshrc when you have one");

    static string EngineFileDescription(string engine) => engine == EngineSettings.Windows
        ? "C:\\ProgramData\\docker\\config\\daemon.json. Dockyard keeps \"hosts\" on its pipe and adds you and docker-users to \"group\". Applying asks for administrator approval and restarts the engine if it runs."
        : "Dockyard adds \"dns\", which container domains need, and this computer's proxy when the file names none. Applying restarts the engine if it runs, which stops containers without a restart policy; one that does not start with the new settings gets the previous ones back.";
}
