using System.Reflection;

namespace Dockyard.Core;

static class Paths
{
    public static readonly string DockerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker");
    public static readonly string DockerExe = Path.Combine(DockerDir, "docker.exe");
    // The Dockyard source's copy where there is one, which a choice against winget or a winget copy Windows will not run leaves.
    public static string DockerCli => File.Exists(DockerExe) ? DockerExe : (OperatingSystem.IsWindows() ? Hosts.Winget.Command(Tools.Docker) : null) ?? DockerExe;
    public static readonly string CredentialHelper = Path.Combine(DockerDir, "docker-credential-wincred.exe");
    public static readonly string DockerdExe = Path.Combine(DockerDir, "dockerd.exe");
    public static readonly string PluginDir = Path.Combine(DockerDir, "cli-plugins");
    // Where the Dockyard source puts a tool that winget did not bring.
    public static string ToolExe(CliTool tool) => Path.Combine(DockerDir, tool.FileName);
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Dockyard");
    public const string AppProcess = "dockyard-toolbox";
    public const string HostProcess = "dockyard-host";
    public static readonly string HostExe = Path.Combine(AppDir, HostProcess + ".exe");
    public static readonly string AppExe = Path.Combine(AppDir, AppProcess + ".exe");
    public static readonly string CommandDir = Path.Combine(AppDir, "bin");
    // The dockyard command, the one component every install has: it installs and updates the rest.
    public static string DockyardCliExe => OperatingSystem.IsWindows() ? Path.Combine(CommandDir, "dockyard.exe")
        : OperatingSystem.IsMacOS() ? Hosts.MacLayout.CliLink
        : "/usr/bin/dockyard";
    // Written when an install has finished, and not by the CLI that is put in place first: an install that stopped after
    // that is still a first install on the next run.
    public static readonly string InstalledMarker = Path.Combine(AppDir, "installed");
    public const string DashboardProcess = "dockyard-dashboard";
    public static readonly string DashboardExe = Path.Combine(AppDir, DashboardProcess + ".exe");
    public static bool HasDashboard => File.Exists(DashboardExe);
    public const string DashboardZipName = "Dockyard.Dashboard.zip";
    public static readonly string DaemonConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "docker", "config");
    public static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Where install.ps1 puts the installer, and asks Defender to allow when it blocks it.
    public static readonly string OneLinerExe = Path.Combine(UserProfile, "Downloads", "Dockyard.exe");
    public static readonly string SettingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard");
    public static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public const string WindowsPipeName = "docker_windows";
    public const string WindowsHost = "npipe:////./pipe/" + WindowsPipeName;
    public const int LinuxPort = Shared.WslPaths.LinuxPort;
    public const string LinuxPipeName = Shared.WslPaths.LinuxPipeName;
    public const string LinuxAgentPipeName = Shared.WslPaths.LinuxAgentPipeName;
    public const string LinuxHost = Shared.WslPaths.LinuxHost;
    public static string EngineTokenFile => Shared.WslPaths.EngineTokenFile;
    public const string ServiceName = "docker";
    public const string Repo = "byhofman/dockyard";
    public const string AppName = "Dockyard";

    public static string Self => Environment.ProcessPath!;
    public static bool IsInstalledCopy => string.Equals(Path.GetFullPath(Self), AppExe, StringComparison.OrdinalIgnoreCase);

    public static string AppVersion =>
        (typeof(Paths).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static readonly string CredentialStoreMarker = Path.Combine(SettingsDir, "credential-store");

    public static readonly string WslConfigFile = Path.Combine(UserProfile, ".wslconfig");
}
