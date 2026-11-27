using Dockyard.Core;

namespace Dockyard.Hosts;

// Where the app lives: packages put it in /usr/lib/dockyard; without apt or dnf the tarball goes in the user's home.
static class LinuxLayout
{
    public const string PackageDir = "/usr/lib/dockyard";
    public static string HomeDir => Path.Combine(Paths.UserProfile, ".local", "lib", "dockyard");
    public static string HomeLink => Path.Combine(Paths.UserProfile, ".local", "bin", "dockyard");
    const string TrayName = "dockyard-tray";
    public const string DashboardName = "dockyard-dashboard";
    public const string Cli = "dockyard-cli";
    // Any of Dockyard's files marks the packaged place: an install can have the CLI or the dashboard without the tray.
    public static string InstallDirWhere(Func<string, bool> exists) =>
        new[] { Cli, DashboardName, TrayName }.Any(name => exists(Path.Combine(PackageDir, name))) ? PackageDir : HomeDir;
    public static string InstallDir => InstallDirWhere(File.Exists);
    public static string InstalledExe => Path.Combine(InstallDir, TrayName);
    public static string Dashboard => Path.Combine(InstallDir, DashboardName);
    public static string CliExe => Path.Combine(InstallDir, Cli);
    public const string InstalledMarker = "/etc/dockyard/installed";
    public const string PolkitRule = "/etc/polkit-1/rules.d/50-dockyard.rules";

    // The polkit rules folder is closed to normal users, so setup leaves a marker they can see.
    public static bool IsInstalled(Func<string, bool> exists)
    {
        var dir = InstallDirWhere(exists);
        return (exists(InstalledMarker) || exists(PolkitRule)) && ThisApp.EitherInstalled(exists, Path.Combine(dir, Cli), Path.Combine(dir, DashboardName));
    }
    public static string VersionFile => Path.Combine(HomeDir, "version");

    public static string DashboardPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-dashboard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard-dashboard_[\d.]+_amd64\.deb$";
    public static string AppPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard_[\d.]+_amd64\.deb$";
    public const string Tarball = @"^Dockyard-[\d.]+-linux-x64\.tar\.gz$";
    public const string CredentialHelper = "/usr/local/bin/docker-credential-secretservice";

    // Only the dashboard's own file: the tray links the same Skia and HarfBuzz libraries, so they stay.
    public static readonly string[] DashboardFiles = [DashboardName];

    public static bool HasDisplay(string? display, string? waylandDisplay) =>
        !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);
}
