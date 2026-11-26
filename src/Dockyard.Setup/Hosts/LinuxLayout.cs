using Dockyard.Core;

namespace Dockyard.Hosts;

// Where the app lives: packages put it in /usr/lib/dockyard; without apt or dnf the tarball goes in the user's home.
static class LinuxLayout
{
    public const string PackageDir = "/usr/lib/dockyard";
    public static string HomeDir => Path.Combine(Paths.UserProfile, ".local", "lib", "dockyard");
    public static string HomeLink => Path.Combine(Paths.UserProfile, ".local", "bin", "dockyard");
    const string TrayName = "dockyard-tray";
    const string DashboardName = "dockyard-dashboard";
    public static string InstallDir => File.Exists(Path.Combine(PackageDir, TrayName)) ? PackageDir : HomeDir;
    public static string InstalledExe => Path.Combine(InstallDir, TrayName);
    public static string Dashboard => Path.Combine(InstallDir, DashboardName);
    public const string Cli = "dockyard-cli";
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
