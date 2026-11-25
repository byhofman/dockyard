using Dockyard.Core;

namespace Dockyard.Hosts;

// Where the app lives: packages put it in /usr/lib/dockyard; without apt or dnf the tarball goes in the user's home.
static class LinuxLayout
{
    public const string PackageDir = "/usr/lib/dockyard";
    public static string HomeDir => Path.Combine(Paths.UserProfile, ".local", "lib", "dockyard");
    public static string HomeLink => Path.Combine(Paths.UserProfile, ".local", "bin", "dockyard");
    // dockyard-toolbox and dockyard-dashboard since 0.9.0; Dockyard and "Dockyard Dashboard" (Dockyard.Dashboard before that) earlier.
    public static readonly string[] ToolboxNames = ["dockyard-toolbox", "Dockyard"];
    public static readonly string[] DashboardNames = ["dockyard-dashboard", "Dockyard Dashboard", "Dockyard.Dashboard"];
    public static string InstallDir => ToolboxNames.Any(n => File.Exists(Path.Combine(PackageDir, n))) ? PackageDir : HomeDir;
    public static string InstalledExe => FirstIn(InstallDir, ToolboxNames);
    public static string Dashboard => FirstIn(InstallDir, DashboardNames);
    public const string Cli = "dockyard-cli";

    static string FirstIn(string dir, string[] names) =>
        names.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists) ?? Path.Combine(dir, names[0]);
    public static string VersionFile => Path.Combine(HomeDir, "version");

    public static string DashboardPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-dashboard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard-dashboard_[\d.]+_amd64\.deb$";
    public static string AppPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard_[\d.]+_amd64\.deb$";
    public const string Tarball = @"^Dockyard-[\d.]+-linux-x64\.tar\.gz$";
    public const string CredentialHelper = "/usr/local/bin/docker-credential-secretservice";
    public const string AppAsset = @"^Dockyard-linux-x64$";

    public static readonly string[] DashboardFiles = [.. DashboardNames, "libSkiaSharp.so", "libHarfBuzzSharp.so"];

    public static bool HasDisplay(string? display, string? waylandDisplay) =>
        !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);
}
