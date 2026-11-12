using Dockyard.Core;

namespace Dockyard.Hosts;

// Where the app lives: packages put it in /usr/lib/dockyard; without apt or dnf the tarball goes in the user's home.
static class LinuxLayout
{
    public const string PackageDir = "/usr/lib/dockyard";
    public static string HomeDir => Path.Combine(Paths.UserProfile, ".local", "lib", "dockyard");
    public static string HomeLink => Path.Combine(Paths.UserProfile, ".local", "bin", "dockyard");
    public static string InstallDir => File.Exists(Path.Combine(PackageDir, "Dockyard")) ? PackageDir : HomeDir;
    public static string InstalledExe => Path.Combine(InstallDir, "Dockyard");
    public static string Dashboard => Path.Combine(InstallDir, "Dockyard Dashboard");
    public static string LegacyDashboard => Path.Combine(InstallDir, "Dockyard.Dashboard");
    public static string VersionFile => Path.Combine(HomeDir, "version");

    public static string DashboardPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-dashboard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard-dashboard_[\d.]+_amd64\.deb$";
    public static string AppPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard_[\d.]+_amd64\.deb$";
    public const string Tarball = @"^Dockyard-[\d.]+-linux-x64\.tar\.gz$";
    public const string CredentialHelper = "/usr/local/bin/docker-credential-secretservice";
    public const string Kind = "/usr/local/bin/kind";
    // Written by setup-linux.sh next to a kind it installed; one without it is the user's own.
    public const string KindMarker = "/etc/dockyard/kind";
    public const string AppAsset = @"^Dockyard-linux-x64$";

    public static readonly string[] DashboardFiles = ["Dockyard Dashboard", "Dockyard.Dashboard", "libSkiaSharp.so", "libHarfBuzzSharp.so"];

    public static bool HasDisplay(string? display, string? waylandDisplay) =>
        !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);
}
