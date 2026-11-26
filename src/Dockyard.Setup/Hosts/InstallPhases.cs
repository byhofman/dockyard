using Dockyard.Core;

namespace Dockyard.Hosts;

// The steps of a host's install as a person knows them, in the order the host runs them, leaving out what the request
// skips. A host says it enters one by logging its marker line, which the facade turns into a numbered step; the line
// works through the elevated worker's log on Windows as it does in this process.
static class InstallPhases
{
    const string Marker = "[dockyard-step] ";

    public const string Dockyard = "Dockyard";
    public const string WindowsEngine = "The Windows engine";
    public const string LinuxEngine = "The Linux engine";
    public const string DockerCli = "docker CLI and plugins";
    public const string KubernetesTools = "Kubernetes tools";
    public const string ContainerDomains = "Container domains";
    public const string TrustedHttps = "Trusted HTTPS";
    public const string MacTools = "docker and the Kubernetes tools";
    public const string Administrator = "Administrator step";
    public const string LinuxVm = "The Linux VM";
    public const string DockerEngine = "Docker Engine and its packages";
    public const string StartingUp = "Starting up";

    public static string Start(string phase) => Marker + phase;

    public static string? Read(string line) => line.StartsWith(Marker, StringComparison.Ordinal) ? line[Marker.Length..] : null;

    // A line the facade reports as a warning: something setup left out and carried on without.
    const string WarningMarker = "[dockyard-warning] ";
    public static string Warning(string line) => WarningMarker + line;
    public static string? ReadWarning(string line) => line.StartsWith(WarningMarker, StringComparison.Ordinal) ? line[WarningMarker.Length..] : null;

    static bool HasKubernetesTools(IReadOnlyCollection<string> without) => Tools.Standalone.Any(t => !without.Contains(t.Id));

    public static string WindowsFeatures(bool windowsEngine, bool linuxEngine) =>
        windowsEngine && linuxEngine ? "Windows features and WSL" : windowsEngine ? "Windows features" : "WSL";

    // The machine half runs elevated first, then the user's half, which holds the Linux engine.
    public static IReadOnlyList<string> Windows(IReadOnlyCollection<string> without, bool trustHttps)
    {
        var windowsEngine = !without.Contains(nameof(Component.Engine));
        var linuxEngine = !without.Contains(nameof(Component.LinuxEngine));
        return
        [
            WindowsFeatures(windowsEngine, linuxEngine),
            .. windowsEngine ? [WindowsEngine] : Array.Empty<string>(),
            DockerCli,
            .. HasKubernetesTools(without) ? [KubernetesTools] : Array.Empty<string>(),
            Dockyard,
            .. linuxEngine ? [ContainerDomains, LinuxEngine] : Array.Empty<string>(),
            .. trustHttps ? [TrustedHttps] : Array.Empty<string>(),
        ];
    }

    // The app comes first: the administrator step links its command, and the VM is started from it.
    public static IReadOnlyList<string> Mac(bool trustHttps) =>
        [Dockyard, MacTools, Administrator, LinuxVm, .. trustHttps ? [TrustedHttps] : Array.Empty<string>()];

    // One administrator run installs the packages and sets up the engine, so they are one step.
    public static IReadOnlyList<string> Linux(IReadOnlyCollection<string> without, bool trustHttps) =>
    [
        Dockyard,
        DockerEngine,
        .. HasKubernetesTools(without) ? [KubernetesTools] : Array.Empty<string>(),
        .. trustHttps ? [TrustedHttps] : Array.Empty<string>(),
    ];
}
