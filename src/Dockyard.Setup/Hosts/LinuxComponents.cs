using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// What Dockyard installs on a Linux host: its two apps, each as a package or, where there is no apt or dnf, as a tarball,
// and the Docker packages it keeps up to date and their plugins.
[SupportedOSPlatform("linux")]
sealed class LinuxComponents(ICommandRunner runner, LinuxRoot root)
{
    static readonly string[] Packages = ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin"];

    public static bool RunsFromInstallLocation =>
        new[] { LinuxLayout.CliDir, LinuxLayout.DashboardDir }.Contains(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparer.Ordinal);

    public static bool HasDashboard => File.Exists(LinuxLayout.Dashboard);
    public static bool HasCli => File.Exists(LinuxLayout.CliExe);

    public async Task<Version?> Installed(string component) => component switch
    {
        "Engine" => Updates.ParseVersion((await runner.Run("dockerd", "--version")).Output),
        "Compose" => Updates.ParseVersion((await runner.Run("docker", "compose", "version", "--short")).Output),
        "Buildx" => Updates.ParseVersion((await runner.Run("docker", "buildx", "version")).Output),
        nameof(Component.DockyardCli) => HasCli ? await AppVersion(App.Cli) : null,
        // The tray came with an app, and is as new as the app whose tray the autostart runs.
        ComponentRules.TrayId => File.Exists(LinuxSetup.AutostartFile) && File.Exists(LinuxLayout.InstalledExe)
            ? await AppVersion(LinuxLayout.InstalledExe.StartsWith(LinuxLayout.DashboardDir + "/", StringComparison.Ordinal) ? App.Dashboard : App.Cli)
            : null,
        ComponentRules.DashboardId => HasDashboard ? Updates.ParseVersion((await runner.Run(LinuxLayout.Dashboard, "--version")).Output) : null,
        _ => null,
    };

    enum App { Cli, Dashboard }

    static string Dir(App app) => app == App.Cli ? LinuxLayout.CliDir : LinuxLayout.DashboardDir;
    static bool IsPackaged(App app) => Dir(app) == (app == App.Cli ? LinuxLayout.PackageDir : LinuxLayout.DashboardPackageDir);
    static string PackageName(App app) => app == App.Cli ? "dockyard" : "dockyard-dashboard";

    public static bool IsPackagedApp(string component) => IsPackaged(component == ComponentRules.DashboardId ? App.Dashboard : App.Cli);

    // Setup runs from a download and can be newer than what is installed.
    async Task<Version?> AppVersion(App app)
    {
        if (!IsPackaged(app))
        {
            var file = LinuxLayout.VersionFileIn(Dir(app));
            return File.Exists(file) ? Updates.ParseVersion(await File.ReadAllTextAsync(file)) : null;
        }
        var dpkg = await root.TryRun("dpkg-query", "-W", "-f", "${Version}", PackageName(app));
        return Updates.ParseVersion(dpkg is { ExitCode: 0 } ? dpkg.Output : (await root.TryRun("rpm", "-q", "--qf", "%{VERSION}", PackageName(app)))?.Output);
    }

    public static async Task<Release?> Latest(string component) => component switch
    {
        "Engine" => Updates.LinuxDocker,
        "Compose" => Updates.LinuxCompose,
        "Buildx" => Updates.LinuxBuildx,
        nameof(Component.DockyardCli) => await Updates.LatestComponentAsset(Updates.CliComponent, LinuxLayout.AppPackage("apt")),
        ComponentRules.DashboardId => await Updates.LatestComponentAsset(Updates.DashboardComponent, LinuxLayout.DashboardPackage("apt")),
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

    public Task StopDashboard() => runner.StopMatching("/dockyard-dashboard$");

    // Each app's package or tarball holds a tray, so the running ones go before one is replaced.
    public async Task StopApps()
    {
        await runner.StopMatching("/dockyard-tray$");
        await StopDashboard();
    }

    public Task UpdateCli(Action<string> log, string? tag) => Place(log, App.Cli, tag);
    public Task UpdateDashboard(Action<string> log, string? tag) => Place(log, App.Dashboard, tag);

    public async Task SetCli(Action<string> log, bool installed)
    {
        if (installed) await Place(log, App.Cli, tag: null);
        else await Remove(log, App.Cli);
    }

    public async Task SetDashboard(Action<string> log, bool installed)
    {
        await StopDashboard();
        if (installed) await Place(log, App.Dashboard, tag: null);
        else await Remove(log, App.Dashboard);
    }

    // Puts the apps chosen in place and takes the others away; one running from where it is installed stays as it is.
    public async Task InstallApps(Action<string> log, bool cli, bool dashboard)
    {
        var change = LinuxLayout.AppsToChange(ThisApp.Current, RunsFromInstallLocation, cli, HasCli, dashboard, HasDashboard);
        if (change.PlaceCli) await Place(log, App.Cli, tag: null);
        if (change.PlaceDashboard) await Place(log, App.Dashboard, tag: null);
        if (change.RemoveDashboard) await Remove(log, App.Dashboard);
        if (change.RemoveCli) await Remove(log, App.Cli);
    }

    async Task Place(Action<string> log, App app, string? tag)
    {
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        if (os.HasPackages) await InstallPackage(log, os.PackageManager, app, tag);
        else await InstallFromTarball(log, app, tag);
    }

    async Task Remove(Action<string> log, App app)
    {
        if (IsPackaged(app))
        {
            var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
            string[] command = os.PackageManager == "dnf"
                ? ["dnf", "-y", "-q", "remove", PackageName(app)]
                : ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "remove", "-y", "-qq", PackageName(app)];
            var r = await root.RunElevated(command);
            foreach (var line in LinuxRoot.Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"Removing {PackageName(app)} failed ({r.ExitCode}): {LinuxRoot.Lines(r.Error).LastOrDefault()}");
        }
        else
        {
            var dir = app == App.Cli ? LinuxLayout.HomeDir : LinuxLayout.DashboardHomeDir;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            if (app == App.Cli && new FileInfo(LinuxLayout.HomeLink).LinkTarget is not null) File.Delete(LinuxLayout.HomeLink);
        }
        log(app == App.Cli ? "Removed the dockyard command" : "Removed the dashboard");
    }

    async Task InstallPackage(Action<string> log, string? packageManager, App app, string? tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            log($"Downloading {PackageName(app)}");
            var package = app == App.Cli
                ? await Updates.DownloadComponentAsset(Updates.CliComponent, LinuxLayout.AppPackage(packageManager), dir, tag)
                : await Updates.DownloadComponentAsset(Updates.DashboardComponent, LinuxLayout.DashboardPackage(packageManager), dir, tag);
            // apt hands local packages to its unprivileged _apt user.
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
            File.SetUnixFileMode(package, (UnixFileMode)0b110_100_100);
            string[] command = packageManager == "dnf"
                ? ["dnf", "-y", "-q", "install", package]
                : ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", "-qq", "--allow-downgrades", package];
            log($"Installing {Path.GetFileName(package)}");
            var r = await root.RunElevated(command);
            foreach (var line in LinuxRoot.Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({r.ExitCode}): {LinuxRoot.Lines(r.Error).LastOrDefault()}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // Neither apt nor dnf: the app's tarball in the user's home, no root needed for the app itself.
    async Task InstallFromTarball(Action<string> log, App app, string? tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        var home = app == App.Cli ? LinuxLayout.HomeDir : LinuxLayout.DashboardHomeDir;
        try
        {
            log($"Downloading {PackageName(app)}");
            var tarball = app == App.Cli
                ? await Updates.DownloadComponentAsset(Updates.CliComponent, LinuxLayout.Tarball, dir, tag)
                : await Updates.DownloadComponentAsset(Updates.DashboardComponent, LinuxLayout.DashboardTarball, dir, tag);
            var extracted = Path.Combine(dir, "extracted");
            Directory.CreateDirectory(extracted);
            await runner.Check("tar", "-xzf", tarball, "-C", extracted);
            var folder = Directory.GetDirectories(extracted).Single();
            Directory.CreateDirectory(home);
            foreach (var file in Directory.GetFiles(folder)) File.Move(file, Path.Combine(home, Path.GetFileName(file)), overwrite: true);
            await File.WriteAllTextAsync(LinuxLayout.VersionFileIn(home), Updates.ParseVersion(Path.GetFileName(tarball))?.ToString() ?? "");
            if (app == App.Cli)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LinuxLayout.HomeLink)!);
                File.Delete(LinuxLayout.HomeLink);
                File.CreateSymbolicLink(LinuxLayout.HomeLink, Path.Combine(home, LinuxLayout.Cli));
            }
            log($"Installed {home}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
