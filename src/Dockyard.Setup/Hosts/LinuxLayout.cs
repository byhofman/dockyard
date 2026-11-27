using Dockyard.Core;

namespace Dockyard.Hosts;

// Where the apps live: each in a folder of its own, packaged in /usr/lib or, without apt or dnf, from its tarball in the
// user's home. The tray, with the Skia and HarfBuzz libraries, ships with either app, so neither needs the other.
static class LinuxLayout
{
    public const string PackageDir = "/usr/lib/dockyard";
    public const string DashboardPackageDir = "/usr/lib/dockyard-dashboard";
    static string HomeLib => Path.Combine(Paths.UserProfile, ".local", "lib");
    public static string HomeDir => Path.Combine(HomeLib, "dockyard");
    public static string DashboardHomeDir => Path.Combine(HomeLib, "dockyard-dashboard");
    public static string HomeLink => Path.Combine(Paths.UserProfile, ".local", "bin", "dockyard");
    public const string TrayName = "dockyard-tray";
    public const string DashboardName = "dockyard-dashboard";
    public const string Cli = "dockyard-cli";

    // An app is where its own program is: a file left behind in the packaged folder does not hide a tarball's app in the
    // home folder.
    static string AppDirWhere(Func<string, bool> exists, string packageDir, string homeDir, string exe) =>
        exists(Path.Combine(packageDir, exe)) ? packageDir : homeDir;
    public static string CliDirWhere(Func<string, bool> exists) => AppDirWhere(exists, PackageDir, HomeDir, Cli);
    public static string DashboardDirWhere(Func<string, bool> exists) => AppDirWhere(exists, DashboardPackageDir, DashboardHomeDir, DashboardName);

    // The dashboard's tray first, as the tray goes by the dashboard first for Dockyard itself.
    public static string TrayWhere(Func<string, bool> exists)
    {
        var dashboards = Path.Combine(DashboardDirWhere(exists), TrayName);
        return exists(dashboards) ? dashboards : Path.Combine(CliDirWhere(exists), TrayName);
    }

    public static string CliDir => CliDirWhere(File.Exists);
    public static string DashboardDir => DashboardDirWhere(File.Exists);
    public static string InstalledExe => TrayWhere(File.Exists);
    public static string Dashboard => Path.Combine(DashboardDir, DashboardName);
    public static string CliExe => Path.Combine(CliDir, Cli);
    public const string InstalledMarker = "/etc/dockyard/installed";
    public const string PolkitRule = "/etc/polkit-1/rules.d/50-dockyard.rules";

    // The polkit rules folder is closed to normal users, so setup leaves a marker they can see.
    public static bool IsInstalled(Func<string, bool> exists) =>
        (exists(InstalledMarker) || exists(PolkitRule))
        && ThisApp.EitherInstalled(exists, Path.Combine(CliDirWhere(exists), Cli), Path.Combine(DashboardDirWhere(exists), DashboardName));

    // A tarball's version, next to the app it belongs to.
    public static string VersionFileIn(string appDir) => Path.Combine(appDir, "version");

    // Setup puts in place the apps chosen that are not there yet, and the app it runs as, from a download, brings its own
    // version along; an app left out goes.
    public static (bool PlaceCli, bool PlaceDashboard, bool RemoveCli, bool RemoveDashboard) AppsToChange(
        RunningApp running, bool runsFromInstall, bool cli, bool cliThere, bool dashboard, bool dashboardThere) =>
    (
        cli && (!cliThere || running == RunningApp.Cli && !runsFromInstall),
        dashboard && (!dashboardThere || running == RunningApp.Dashboard && !runsFromInstall),
        !cli && cliThere,
        !dashboard && dashboardThere
    );

    // The running app's folder last: it is what finishes an uninstall that stopped halfway.
    public static IReadOnlyList<string> HomeDirsToRemove(string running) =>
        [.. new[] { HomeDir, DashboardHomeDir }.OrderBy(dir => running.StartsWith(dir + "/", StringComparison.Ordinal))];

    public static string DashboardPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-dashboard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard-dashboard_[\d.]+_amd64\.deb$";
    public static string AppPackage(string? packageManager) =>
        packageManager == "dnf" ? @"^dockyard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard_[\d.]+_amd64\.deb$";
    // Each app's tarball holds one folder: the app, the tray and the libraries.
    public const string Tarball = @"^Dockyard-[\d.]+-linux-x64\.tar\.gz$";
    public const string DashboardTarball = @"^dockyard-dashboard-[\d.]+-linux-x64\.tar\.gz$";
    public const string CredentialHelper = "/usr/local/bin/docker-credential-secretservice";

    public static bool HasDisplay(string? display, string? waylandDisplay) =>
        !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);
}
