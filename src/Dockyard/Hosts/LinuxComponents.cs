using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// What Dockyard installs on a Linux host: its own app and dashboard (as packages, or a tarball where there is no apt or
// dnf), the Docker packages it keeps up to date, their plugins, and kind.
[SupportedOSPlatform("linux")]
sealed class LinuxComponents(ICommandRunner runner, LinuxRoot root)
{
    static readonly string[] Packages = ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin"];

    public static bool RunsFromInstallLocation => string.Equals(
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), LinuxLayout.InstallDir, StringComparison.Ordinal);

    public static bool HasDashboard => File.Exists(LinuxLayout.Dashboard) || File.Exists(LinuxLayout.LegacyDashboard);

    public async Task<Version?> Installed(string component) => component switch
    {
        "Engine" => Updates.ParseVersion((await runner.Run("dockerd", "--version")).Output),
        "Compose" => Updates.ParseVersion((await runner.Run("docker", "compose", "version", "--short")).Output),
        "Buildx" => Updates.ParseVersion((await runner.Run("docker", "buildx", "version")).Output),
        Core.Kind.Id => File.Exists(LinuxLayout.Kind) ? Updates.ParseVersion((await runner.Run(LinuxLayout.Kind, "version")).Output) : null,
        "App" => await InstalledAppVersion(),
        ComponentRules.DashboardId => await InstalledDashboardVersion(),
        _ => null,
    };

    // Asking the old binary would open its window, since it has no --version: it came with the app's version.
    async Task<Version?> InstalledDashboardVersion()
    {
        if (File.Exists(LinuxLayout.Dashboard)) return Updates.ParseVersion((await runner.Run(LinuxLayout.Dashboard, "--version")).Output);
        return HasDashboard ? await InstalledAppVersion() : null;
    }

    // The wizard runs from a download and can be newer than what is installed.
    async Task<Version?> InstalledAppVersion()
    {
        if (!File.Exists(LinuxLayout.InstalledExe)) return null;
        if (LinuxLayout.InstallDir == LinuxLayout.HomeDir)
            return File.Exists(LinuxLayout.VersionFile) ? Updates.ParseVersion(await File.ReadAllTextAsync(LinuxLayout.VersionFile)) : null;
        var dpkg = await root.TryRun("dpkg-query", "-W", "-f", "${Version}", "dockyard");
        return Updates.ParseVersion(dpkg is { ExitCode: 0 } ? dpkg.Output : (await root.TryRun("rpm", "-q", "--qf", "%{VERSION}", "dockyard"))?.Output);
    }

    public static async Task<Release?> Latest(string component) => component switch
    {
        "Engine" => Updates.LinuxDocker,
        "Compose" => Updates.LinuxCompose,
        "Buildx" => Updates.LinuxBuildx,
        Core.Kind.Id => Core.Kind.Release,
        "App" or ComponentRules.DashboardId => await Updates.LatestApp(LinuxLayout.AppAsset),
        _ => null,
    };

    public async Task UpdateDockerPackages(Action<string> log)
    {
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        string[] command = os.PackageManager switch
        {
            "apt" => ["apt-get", "install", "-y", "--only-upgrade", .. Packages],
            "dnf" => ["dnf", "-y", "upgrade", .. Packages],
            _ => throw new InvalidOperationException("Update Docker with your distribution's package manager"),
        };
        log($"Updating Docker packages with {command[0]}");
        var r = await root.RunElevated(["env", "DEBIAN_FRONTEND=noninteractive", .. command]);
        foreach (var line in LinuxRoot.Lines(r.Output + r.Error).TakeLast(6)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({r.ExitCode})");
    }

    static string PluginPackage(string? packageManager, string component) => (packageManager, component) switch
    {
        ("apt" or "dnf", "Compose") => "docker-compose-plugin",
        ("apt" or "dnf", "Buildx") => "docker-buildx-plugin",
        (_, "Compose") => "docker-compose",
        _ => "docker-buildx",
    };

    public async Task ChangePlugin(Action<string> log, string component, bool installed)
    {
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        var family = os.PackageManager ?? new[] { os.Id }.Concat(os.IdLike).Select(id => id switch { "arch" => "pacman", "opensuse" or "suse" or "sles" => "zypper", _ => null }).FirstOrDefault(f => f is not null);
        if (family is null && os.Id.StartsWith("opensuse", StringComparison.Ordinal)) family = "zypper";
        var package = PluginPackage(family, component);
        if (installed)
        {
            await root.RunSetup(log, "add-plugin", package);
            return;
        }
        string[] command = family switch
        {
            "apt" => ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "remove", "-y", "-qq", package],
            "dnf" => ["dnf", "-y", "-q", "remove", package],
            "pacman" => ["pacman", "-R", "--noconfirm", package],
            "zypper" => ["zypper", "--non-interactive", "remove", package],
            _ => throw new InvalidOperationException($"Remove {package} with your distribution's package manager"),
        };
        var r = await root.RunElevated(command);
        foreach (var line in LinuxRoot.Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Removing {package} failed ({r.ExitCode}): {LinuxRoot.Lines(r.Error).LastOrDefault()}");
        log($"Removed {package}");
    }

    public async Task StopDashboard() => await root.TryRun("pkill", "-f", "/Dockyard[. ]Dashboard$");

    public async Task SetDashboard(Action<string> log, bool installed)
    {
        await StopDashboard();
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        if (LinuxLayout.InstallDir == LinuxLayout.HomeDir)
        {
            if (installed) await InstallFromTarball(log, dashboard: true, tag: null);
            else
            {
                foreach (var name in LinuxLayout.DashboardFiles) File.Delete(Path.Combine(LinuxLayout.HomeDir, name));
                log("Removed the dashboard");
            }
            return;
        }
        if (installed)
        {
            // A wizard newer than what is installed brings the matching app along: the dashboard package depends on its exact version.
            var withApp = await InstalledAppVersion() != Updates.ParseVersion(Paths.AppVersion);
            await InstallPackages(log, os.PackageManager, app: withApp, dashboard: true, tag: null);
        }
        else await RemoveDashboardPackage(log, os.PackageManager);
    }

    // Puts the app in place. A copy running from where it is installed only has to line up the dashboard.
    public async Task InstallApp(Action<string> log, bool dashboard, string? tag)
    {
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        var replace = tag is not null || !RunsFromInstallLocation;
        if (!os.HasPackages)
        {
            if (replace) await InstallFromTarball(log, dashboard, tag);
            else if (HasDashboard != dashboard) await SetDashboard(log, dashboard);
            return;
        }
        if (replace) await InstallPackages(log, os.PackageManager, app: true, dashboard, tag);
        if (HasDashboard && !dashboard) await RemoveDashboardPackage(log, os.PackageManager);
        else if (!HasDashboard && dashboard) await InstallPackages(log, os.PackageManager, app: false, dashboard: true, tag);
    }

    async Task InstallPackages(Action<string> log, string? packageManager, bool app, bool dashboard, string? tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            List<string> packages = [];
            log("Downloading Dockyard");
            if (app) packages.Add(await Updates.ReleaseAssetOrLocal(LinuxLayout.AppPackage(packageManager), dir, tag));
            if (dashboard) packages.Add(await Updates.ReleaseAssetOrLocal(LinuxLayout.DashboardPackage(packageManager), dir, tag));
            // apt hands local packages to its unprivileged _apt user.
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
            foreach (var package in packages) File.SetUnixFileMode(package, (UnixFileMode)0b110_100_100);
            string[] command = packageManager == "dnf"
                ? ["dnf", "-y", "-q", "install", .. packages]
                : ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "-qq", "--allow-downgrades", .. packages];
            log($"Installing {string.Join(", ", packages.Select(Path.GetFileName))}");
            var r = await root.RunElevated(command);
            foreach (var line in LinuxRoot.Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({r.ExitCode}): {LinuxRoot.Lines(r.Error).LastOrDefault()}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    async Task RemoveDashboardPackage(Action<string> log, string? packageManager)
    {
        string[] command = packageManager == "dnf"
            ? ["dnf", "-y", "-q", "remove", "dockyard-dashboard"]
            : ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "remove", "-y", "-qq", "dockyard-dashboard"];
        var r = await root.RunElevated(command);
        foreach (var line in LinuxRoot.Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Removing the dashboard failed ({r.ExitCode}): {LinuxRoot.Lines(r.Error).LastOrDefault()}");
        log("Removed the dashboard");
    }

    // Neither apt nor dnf: the release's tarball in the user's home, no root needed for the app itself.
    async Task InstallFromTarball(Action<string> log, bool dashboard, string? tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            log("Downloading Dockyard");
            var tarball = await Updates.ReleaseAssetOrLocal(LinuxLayout.Tarball, dir, tag);
            await runner.Check("tar", "-xzf", tarball, "-C", dir);
            Directory.CreateDirectory(LinuxLayout.HomeDir);
            foreach (var file in Directory.GetFiles(Path.Combine(dir, "Dockyard")))
            {
                var name = Path.GetFileName(file);
                if (dashboard || !LinuxLayout.DashboardFiles.Contains(name)) File.Move(file, Path.Combine(LinuxLayout.HomeDir, name), overwrite: true);
            }
            File.Delete(Path.Combine(LinuxLayout.HomeDir, "Dockyard.Dashboard"));
            if (!dashboard)
                foreach (var name in LinuxLayout.DashboardFiles) File.Delete(Path.Combine(LinuxLayout.HomeDir, name));
            await File.WriteAllTextAsync(LinuxLayout.VersionFile, Updates.ParseVersion(Path.GetFileName(tarball))?.ToString() ?? "");
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxLayout.HomeLink)!);
            File.Delete(LinuxLayout.HomeLink);
            File.CreateSymbolicLink(LinuxLayout.HomeLink, Path.Combine(LinuxLayout.HomeDir, "Dockyard"));
            log($"Installed {LinuxLayout.HomeDir}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // Root puts it in /usr/local/bin, where every shell finds it, and only over a kind that Dockyard put there.
    public async Task InstallKind(Action<string> log)
    {
        var dir = Directory.CreateTempSubdirectory("dockyard-kind").FullName;
        try
        {
            var file = Path.Combine(dir, "kind");
            await Core.Kind.Download(file);
            await root.RunSetup(log, "add-kind", "", file);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public Task RemoveKind(Action<string> log) => root.RunSetup(log, "remove-kind");
}
