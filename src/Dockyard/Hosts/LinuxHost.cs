using System.Reflection;
using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

sealed record OsRelease(string Id, IReadOnlyList<string> IdLike, string? Codename)
{
    public static OsRelease Parse(string text)
    {
        var values = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..].Trim('"', '\'')))
            .GroupBy(x => x.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);
        return new OsRelease(
            values.GetValueOrDefault("ID", "linux"),
            values.GetValueOrDefault("ID_LIKE", "").Split(' ', StringSplitOptions.RemoveEmptyEntries),
            values.GetValueOrDefault("UBUNTU_CODENAME") ?? values.GetValueOrDefault("VERSION_CODENAME"));
    }

    public bool HasPackages => PackageManager is "apt" or "dnf";

    public string? PackageManager => new[] { Id }.Concat(IdLike).Select(id => id switch
    {
        "ubuntu" or "debian" => "apt",
        "fedora" or "rhel" or "centos" => "dnf",
        _ => null,
    }).FirstOrDefault(p => p is not null);
}

// How the steps that need root get it, in order of least interruption. A silent setup without a terminal or a
// desktop has no one to ask: then there is no way, and setup says so instead of hanging on a prompt.
static class LinuxElevation
{
    public static string[]? Prefix(bool isRoot, bool passwordlessSudo, bool hasDisplay, bool hasPkexec, bool hasSudo, bool hasTerminal)
    {
        if (isRoot) return [];
        if (passwordlessSudo) return ["sudo", "-n"];
        if (hasDisplay && hasPkexec) return ["pkexec"];
        if (hasTerminal && hasSudo) return ["sudo"];
        if (hasTerminal && hasPkexec) return ["pkexec"];
        return null;
    }
}

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
    public const string AppAsset = @"^Dockyard-linux-x64$";

    public static readonly string[] DashboardFiles = ["Dockyard Dashboard", "Dockyard.Dashboard", "libSkiaSharp.so", "libHarfBuzzSharp.so"];

    public static bool HasDisplay(string? display, string? waylandDisplay) =>
        !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);
}

[SupportedOSPlatform("linux")]
sealed class LinuxHost(ICommandRunner runner) : IEngineHost
{
    public const string Engine = "linux";
    const string PolkitRule = "/etc/polkit-1/rules.d/50-dockyard.rules";
    static readonly string[] Packages = ["docker-ce", "docker-ce-cli", "containerd.io", "docker-buildx-plugin", "docker-compose-plugin"];

    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new(Engine, "Linux", "Docker Engine", "system dockerd, started on demand", "default", "unix:///var/run/docker.sock")];

    public IReadOnlyList<ComponentInfo> Components { get; } =
    [
        new("Engine", "Docker Engine & CLI", RequiredBecause: "it is what Dockyard starts and stops. It stays yours to remove with your package manager, also after uninstalling Dockyard.", RequiredFlag: "linux-engine"),
        new("Compose", "Docker Compose", Flag: "compose", Note: "Needs Docker. The dashboard's Compose actions use it."),
        new("Buildx", "Docker Buildx", Flag: "buildx", Note: "Needs Docker. docker build uses it."),
        new("App", "Dockyard", RequiredBecause: "it is what runs everything here. Uninstall removes it."),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional. Its package is versioned with Dockyard, so updating it updates both."),
    ];

    // The polkit rules folder is closed to normal users, so setup leaves a marker they can see.
    public bool IsInstalled => File.Exists("/etc/dockyard/installed") || File.Exists(PolkitRule);

    public static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config : Path.Combine(Paths.UserProfile, ".config"),
        "autostart", "dockyard.desktop");

    public async Task<bool> IsRunning(string engine) =>
        (await runner.Run("systemctl", "is-active", "--quiet", "docker.service")).ExitCode == 0;

    public async Task Start(string engine)
    {
        await runner.Check("systemctl", "start", "docker.socket", "docker.service");
        await Wait.For(() => File.Exists("/var/run/docker.sock"), TimeSpan.FromSeconds(30), "Docker engine");
    }

    // The socket is stopped too: otherwise any client (the dashboard reconnecting) would start the engine again right away.
    public Task Stop(string engine) => runner.Check("systemctl", "stop", "docker.socket", "docker.service", "containerd.service");

    public string ActiveEngine() => Engine;

    public Task UseContext(string engine) => runner.Check("docker", "context", "use", "default");

    public async Task<Version?> Installed(string component) => component switch
    {
        "Engine" => Updates.ParseVersion((await runner.Run("dockerd", "--version")).Output),
        "Compose" => Updates.ParseVersion((await runner.Run("docker", "compose", "version", "--short")).Output),
        "Buildx" => Updates.ParseVersion((await runner.Run("docker", "buildx", "version")).Output),
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

    static string PluginPackage(string? packageManager, string component) => (packageManager, component) switch
    {
        ("apt" or "dnf", "Compose") => "docker-compose-plugin",
        ("apt" or "dnf", "Buildx") => "docker-buildx-plugin",
        (_, "Compose") => "docker-compose",
        _ => "docker-buildx",
    };

    async Task ChangePlugin(Action<string> log, string component, bool installed)
    {
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        var family = os.PackageManager ?? new[] { os.Id }.Concat(os.IdLike).Select(id => id switch { "arch" => "pacman", "opensuse" or "suse" or "sles" => "zypper", _ => null }).FirstOrDefault(f => f is not null);
        if (family is null && os.Id.StartsWith("opensuse", StringComparison.Ordinal)) family = "zypper";
        var package = PluginPackage(family, component);
        if (installed)
        {
            await RunSetup(log, "add-plugin", package);
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
        var r = await RunElevated(command);
        foreach (var line in Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Removing {package} failed ({r.ExitCode}): {Lines(r.Error).LastOrDefault()}");
        log($"Removed {package}");
    }

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        if (component == ComponentRules.DashboardId) return await SetDashboard(log, installed);
        if (component is not ("Compose" or "Buildx")) throw new InvalidOperationException($"{component} cannot be changed here");
        await ChangePlugin(log, component, installed);
        return false;
    }

    async Task StopDashboard() => await TryRun("pkill", "-f", "/Dockyard[. ]Dashboard$");

    // The wizard runs from a download and can be newer than what is installed.
    async Task<Version?> InstalledAppVersion()
    {
        if (!File.Exists(LinuxLayout.InstalledExe)) return null;
        if (LinuxLayout.InstallDir == LinuxLayout.HomeDir)
            return File.Exists(LinuxLayout.VersionFile) ? Updates.ParseVersion(await File.ReadAllTextAsync(LinuxLayout.VersionFile)) : null;
        var dpkg = await TryRun("dpkg-query", "-W", "-f", "${Version}", "dockyard");
        return Updates.ParseVersion(dpkg is { ExitCode: 0 } ? dpkg.Output : (await TryRun("rpm", "-q", "--qf", "%{VERSION}", "dockyard"))?.Output);
    }

    async Task<ShellResult?> TryRun(string file, params string[] args)
    {
        try { return await runner.Run(file, args); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public async Task<Release?> Latest(string component) => component switch
    {
        "Engine" => await Updates.LatestStatic(Updates.LinuxStaticIndex, "tgz"),
        "Compose" => await Updates.LatestGitHub("docker/compose", Updates.LinuxComposeAsset),
        "Buildx" => await Updates.LatestGitHub("docker/buildx", Updates.LinuxBuildxAsset),
        "App" or ComponentRules.DashboardId => await Updates.LatestApp(LinuxLayout.AppAsset),
        _ => null,
    };

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        if (components.Any(c => c.Component is "Engine" or "Compose" or "Buildx"))
        {
            var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
            string[] command = os.PackageManager switch
            {
                "apt" => ["apt-get", "install", "-y", "--only-upgrade", .. Packages],
                "dnf" => ["dnf", "-y", "upgrade", .. Packages],
                _ => throw new InvalidOperationException("Update Docker with your distribution's package manager"),
            };
            log($"Updating Docker packages with {command[0]}");
            var r = await RunElevated(["env", "DEBIAN_FRONTEND=noninteractive", .. command]);
            foreach (var line in Lines(r.Output + r.Error).TakeLast(6)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({r.ExitCode})");
        }
        if (components.FirstOrDefault(c => c.Component is "App" or ComponentRules.DashboardId) is { Release: { } app })
        {
            await StopDashboard();
            await InstallApp(log, HasDashboard, app.Tag);
            // The tray still holds the single-instance lock, so the new one starts just after this process exits.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", $"sleep 2; exec \"{LinuxLayout.InstalledExe}\" --tray"]) { UseShellExecute = false })?.Dispose();
            return true;
        }
        return false;
    }

    public bool RunsFromInstallLocation => string.Equals(
        Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), LinuxLayout.InstallDir, StringComparison.Ordinal);

    public bool HasDashboard => File.Exists(LinuxLayout.Dashboard) || File.Exists(LinuxLayout.LegacyDashboard);

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
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
            return false;
        }
        if (installed)
        {
            // A wizard newer than what is installed brings the matching app along: the dashboard package depends on its exact version.
            var withApp = await InstalledAppVersion() != Updates.ParseVersion(Paths.AppVersion);
            await InstallPackages(log, os.PackageManager, app: withApp, dashboard: true, tag: null);
        }
        else await RemoveDashboardPackage(log, os.PackageManager);
        return false;
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        if (HasDashboard && !request.Dashboard) await StopDashboard();
        await InstallApp(log, request.Dashboard, tag: null);
        await InstallWithAgent(log);
        if (Environment.UserName == "root")
            log("Running as root: the tray autostart is set up the first time you run setup as your own user");
        else
        {
            await WriteAutostart();
            log($"{Paths.AppName} autostarts at login ({AutostartFile})");
        }
        await SetUpCredentialStore(log);
        if (request.TrustHttps) await Https.Enable(this, log, request.Silent);
        await TryRun("gnome-extensions", "enable", "appindicatorsupport@rgcjonas.gmail.com");
        if ((await TryRun("docker", "context", "show"))?.Output.Trim() is { Length: > 0 } current && current != "default")
        {
            await TryRun("docker", "context", "use", "default");
            log($"The docker context was '{current}'; Dockyard manages 'default', which is selected now");
        }
        return false;
    }

    public void StartApp()
    {
        if (!LinuxLayout.HasDisplay(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) return;
        Shell.Spawn(LinuxLayout.InstalledExe, "--tray").Dispose();
    }

    public string DoneMessage(bool reboot) => "Done. Dockyard and the docker command are ready.";

    public async Task Uninstall(Action<string> log, bool removeData)
    {
        if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
        await CredentialStore.Forget(Paths.CredentialStoreMarker, LinuxLayout.CredentialHelper, log);
        await Https.Disable(this, log);
        await UninstallWithConfig(log);
        if (Directory.Exists(LinuxLayout.HomeDir)) Directory.Delete(LinuxLayout.HomeDir, recursive: true);
        if (new FileInfo(LinuxLayout.HomeLink).LinkTarget is not null) File.Delete(LinuxLayout.HomeLink);
        if (Directory.Exists(Paths.SettingsDir)) Directory.Delete(Paths.SettingsDir, recursive: true);
        if (removeData) log("Images and containers belong to the system's Docker and were kept; docker system prune removes them");
    }

    public void ReleaseResources() { }

    static string SetupUser => Environment.UserName == "root" ? Environment.GetEnvironmentVariable("SUDO_USER") ?? "" : Environment.UserName;

    public async Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent)
    {
        try
        {
            // A browser run by this user reads its own store on Linux, where it exists; the system's covers curl and the rest.
            await RunSetup(log, "trust-ca", "", certificateFile);
            var nss = Path.Combine(Paths.UserProfile, ".pki", "nssdb");
            if (Environment.UserName != "root" && Directory.Exists(nss))
                await TryRun("certutil", "-d", "sql:" + nss, "-A", "-t", "C,,", "-n", LocalCa.SubjectName, "-i", certificateFile);
            return true;
        }
        catch (InvalidOperationException e)
        {
            log(e.Message);
            return false;
        }
    }

    public async Task UntrustCa(Action<string> log, string certificateFile)
    {
        if (!File.Exists(certificateFile)) return;
        var nss = Path.Combine(Paths.UserProfile, ".pki", "nssdb");
        if (Directory.Exists(nss)) await TryRun("certutil", "-d", "sql:" + nss, "-D", "-n", LocalCa.SubjectName);
        await RunSetup(log, "untrust-ca");
    }

    const string DaemonJson = "/etc/docker/daemon.json";

    // The setup script runs as root but has no JSON tools, so the merged file is prepared here and handed to it.
    async Task InstallWithAgent(Action<string> log)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var agent = await GuestBundle.ExtractAgent(dir) ?? "";
            var existing = File.Exists(DaemonJson) ? await File.ReadAllTextAsync(DaemonJson) : null;
            var config = Path.Combine(dir, "daemon.json");
            await File.WriteAllTextAsync(config, agent.Length == 0 ? existing ?? "{}" : EngineConfig.WithDockyard(existing, HostProxySettings.FromEnvironment(Environment.GetEnvironmentVariable)));
            // Without systemd nothing runs the agent, and a dns entry naming it would leave containers without any DNS.
            if (!Directory.Exists("/run/systemd/system")) await File.WriteAllTextAsync(config, existing ?? "{}");
            else if (agent.Length > 0 && EngineConfig.DnsIsSomeoneElses(existing))
                log($"{DaemonJson} sets its own dns, so containers do without host.docker.internal; remove that setting to get it");
            // Root reads these through a path a normal user's temp folder may keep from it otherwise.
            File.SetUnixFileMode(dir, (UnixFileMode)0b111_101_101);
            await RunSetup(log, "install", SetupUser, agent, config);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    async Task UninstallWithConfig(Action<string> log)
    {
        var config = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}.json");
        try
        {
            if (File.Exists(DaemonJson)) await File.WriteAllTextAsync(config, EngineConfig.WithoutDockyard(await File.ReadAllTextAsync(DaemonJson)));
            await RunSetup(log, "uninstall", "", File.Exists(config) ? config : "");
        }
        finally
        {
            File.Delete(config);
        }
    }

    // A server without a desktop has no secret service to keep logins in. Setting the store anyway would make docker login
    // fail outright, so the helper has to answer first.
    async Task SetUpCredentialStore(Action<string> log)
    {
        if (Environment.UserName == "root" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            log("No desktop session here, so no OS credential store: docker login keeps Docker's default (base64 in ~/.docker/config.json)");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            if (!File.Exists(LinuxLayout.CredentialHelper))
            {
                var release = await Updates.LatestGitHub(Paths.CredentialHelpersRepo, "docker-credential-secretservice-{tag}.linux-amd64");
                Directory.CreateDirectory(dir);
                var download = Path.Combine(dir, "helper");
                await Updates.DownloadFile(release.DownloadUrl!, download);
                await RunElevated(["install", "-m", "0755", download, LinuxLayout.CredentialHelper]);
            }
            if ((await TryRun(LinuxLayout.CredentialHelper, "list")) is { ExitCode: 0 })
                await CredentialStore.Adopt(log, "secretservice", LinuxLayout.CredentialHelper, Paths.CredentialStoreMarker);
            else
                log("The desktop has no working secret service, so docker login keeps Docker's default (base64 in ~/.docker/config.json)");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            log($"The credential store was not set up ({e.Message}); running the wizard again retries it");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // Puts the app in place. A copy running from where it is installed only has to line up the dashboard.
    async Task InstallApp(Action<string> log, bool dashboard, string? tag)
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
            var r = await RunElevated(command);
            foreach (var line in Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({r.ExitCode}): {Lines(r.Error).LastOrDefault()}");
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
        var r = await RunElevated(command);
        foreach (var line in Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Removing the dashboard failed ({r.ExitCode}): {Lines(r.Error).LastOrDefault()}");
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

    async Task<ShellResult> RunElevated(string[] command)
    {
        var prefix = await ElevationPrefix() ?? throw new InvalidOperationException(
            "This step needs root, and there is no way to ask here: no passwordless sudo, desktop or terminal. Run it as root or from a terminal.");
        string[] full = [.. prefix, .. command];
        return await runner.Run(full[0], full[1..]);
    }

    async Task<string[]?> ElevationPrefix() => LinuxElevation.Prefix(
        isRoot: Environment.UserName == "root",
        passwordlessSudo: (await TryRun("sudo", "-n", "true"))?.ExitCode == 0,
        hasDisplay: LinuxLayout.HasDisplay(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
        hasPkexec: OnPath("pkexec"),
        hasSudo: OnPath("sudo"),
        hasTerminal: HasTerminal());

    static bool OnPath(string file) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, file)));

    static bool HasTerminal()
    {
        try
        {
            using var tty = File.Open("/dev/tty", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    async Task WriteAutostart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        await File.WriteAllTextAsync(AutostartFile, DesktopEntry.Autostart(LinuxLayout.InstalledExe));
    }

    async Task RunSetup(Action<string> log, params string[] args)
    {
        var script = Path.Combine(Path.GetTempPath(), $"dockyard-setup-{Guid.NewGuid():N}.sh");
        await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-linux.sh")!)
        using (var reader = new StreamReader(stream))
            await File.WriteAllTextAsync(script, (await reader.ReadToEndAsync()).Replace("\r\n", "\n"));
        try
        {
            log("Administrator step");
            var r = await RunElevated(["sh", script, .. args]);
            foreach (var line in Lines(r.Output + r.Error)) log("  " + line);
            if (r.ExitCode == 126 || r.ExitCode == 127) throw new InvalidOperationException("Administrator approval was declined");
            if (r.ExitCode != 0) throw new InvalidOperationException($"Setup failed ({r.ExitCode})");
        }
        finally
        {
            File.Delete(script);
        }
    }

    static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);
}

static class DesktopEntry
{
    public static string Autostart(string exe) =>
        $"[Desktop Entry]\nType=Application\nName=Dockyard\nComment=Docker engine tray\nExec=\"{exe}\" --tray\nIcon=dockyard\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
}
