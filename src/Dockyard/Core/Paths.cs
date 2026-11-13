using System.Reflection;

namespace Dockyard.Core;

static class Paths
{
    public static readonly string DockerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker");
    public static readonly string DockerExe = Path.Combine(DockerDir, "docker.exe");
    public static readonly string CredentialHelper = Path.Combine(DockerDir, "docker-credential-wincred.exe");
    public static readonly string DockerdExe = Path.Combine(DockerDir, "dockerd.exe");
    public static readonly string PluginDir = Path.Combine(DockerDir, "cli-plugins");
    public static readonly string KindExe = Path.Combine(DockerDir, "kind.exe");
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dockyard");
    public static readonly string AppExe = Path.Combine(AppDir, "Dockyard.exe");
    public static readonly string DashboardExe = Path.Combine(AppDir, DashboardProcess + ".exe");
    public static readonly string LegacyDashboardExe = Path.Combine(AppDir, LegacyDashboardProcess + ".exe");
    public const string DashboardProcess = "Dockyard Dashboard";
    // What installs up to v0.2.1 have, running or not.
    public const string LegacyDashboardProcess = "Dockyard.Dashboard";
    // The asset keeps its name: scripts and trays already out there ask for it.
    public const string DashboardZipName = "Dockyard.Dashboard.zip";
    public static readonly string DaemonConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "docker", "config");
    public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Where install.ps1 puts the installer, and asks Defender to allow when it blocks it.
    public static readonly string OneLinerExe = Path.Combine(UserProfile, "Downloads", "Dockyard.exe");
    public static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard");
    public static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public static readonly string LegacyTrayDir = Path.Combine(DockerDir, "tray");
    public static readonly string LegacySettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockerTray");
    public const string LegacyName = "DockerTray";

    public const string WindowsPipeName = "docker_windows";
    public const string WindowsHost = "npipe:////./pipe/" + WindowsPipeName;
    // Where dockyard-agent serves the Linux engine to Windows, to the tray's token only.
    public const int LinuxPort = 2375;
    public const string LinuxPipeName = "dockyard_linux";
    public const string LinuxAgentPipeName = "dockyard_linux_agent";
    public const string LinuxHost = "npipe:////./pipe/" + LinuxPipeName;
    // What the linux context pointed at up to v0.4: an open port every Windows user and web page could reach.
    public const string LegacyLinuxHost = "tcp://127.0.0.1:2375";
    public static readonly string EngineTokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "engine.token");
    public const string ServiceName = "docker";
    public const string Repo = "byhofman/dockyard";
    public const string AppName = "Dockyard";

    public static string Self => Environment.ProcessPath!;
    public static bool IsInstalledCopy => string.Equals(Path.GetFullPath(Self), AppExe, StringComparison.OrdinalIgnoreCase);

    public static string AppVersion =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static readonly string CredentialStoreMarker = Path.Combine(SettingsDir, "credential-store");

    public static readonly string WslConfigFile = Path.Combine(UserProfile, ".wslconfig");
}
