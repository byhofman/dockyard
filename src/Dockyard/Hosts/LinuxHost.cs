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

    public string? PackageManager => new[] { Id }.Concat(IdLike).Select(id => id switch
    {
        "ubuntu" or "debian" => "apt",
        "fedora" or "rhel" or "centos" => "dnf",
        _ => null,
    }).FirstOrDefault(p => p is not null);
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
        new("Engine", "Docker Engine & CLI"),
        new("Compose", "Docker Compose"),
        new("Buildx", "Docker Buildx"),
        new("App", "Dockyard"),
    ];

    public bool IsInstalled => File.Exists(PolkitRule);

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
        "App" => Updates.ParseVersion(Paths.AppVersion),
        _ => null,
    };

    public async Task<Release?> Latest(string component) => component switch
    {
        "Engine" => await Updates.LatestStatic(Updates.LinuxStaticIndex, "tgz"),
        "Compose" => await Updates.LatestGitHub("docker/compose", Updates.LinuxComposeAsset),
        "Buildx" => await Updates.LatestGitHub("docker/buildx", Updates.LinuxBuildxAsset),
        "App" => await Updates.LatestApp(),
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
            var r = await runner.Run("pkexec", ["env", "DEBIAN_FRONTEND=noninteractive", .. command]);
            foreach (var line in Lines(r.Output + r.Error).TakeLast(6)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"{command[0]} failed ({r.ExitCode})");
        }
        if (components.Any(c => c.Component == "App"))
            log("Update Dockyard by running the install script again: curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh");
        return false;
    }

    public bool HasDashboard => File.Exists(Path.Combine(AppContext.BaseDirectory, "Dockyard.Dashboard"));

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        var os = OsRelease.Parse(await File.ReadAllTextAsync("/etc/os-release"));
        string? package = null;
        if (installed)
        {
            // The dashboard package of this release, downloaded from GitHub.
            var pattern = os.PackageManager == "dnf" ? @"^dockyard-dashboard-[\d.]+-1\.x86_64\.rpm$" : @"^dockyard-dashboard_[\d.]+_amd64\.deb$";
            log("Downloading the dashboard");
            package = await Updates.DownloadReleaseAsset(pattern, Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}"), isPattern: true);
            File.SetUnixFileMode(Path.GetDirectoryName(package)!, (UnixFileMode)0b111_101_101);
        }
        string[] command = (os.PackageManager, installed) switch
        {
            ("apt", true) => ["env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y", package!],
            ("apt", false) => ["apt-get", "remove", "-y", "dockyard-dashboard"],
            ("dnf", true) => ["dnf", "-y", "install", package!],
            ("dnf", false) => ["dnf", "-y", "remove", "dockyard-dashboard"],
            _ => throw new InvalidOperationException("Install or remove the dockyard-dashboard package with your package manager"),
        };
        try
        {
            var r = await runner.Run("pkexec", command);
            foreach (var line in Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
            if (r.ExitCode != 0) throw new InvalidOperationException($"Package manager failed ({r.ExitCode})");
        }
        finally
        {
            if (package is not null) Directory.Delete(Path.GetDirectoryName(package)!, recursive: true);
        }
        return false;
    }

    public async Task<bool> Install(Action<string> log, bool engineOnly)
    {
        await RunSetup(log, "install", Environment.UserName);
        await WriteAutostart();
        log($"{Paths.AppName} autostarts at login ({AutostartFile})");
        if ((await runner.Run("docker", "context", "show")).Output.Trim() is { Length: > 0 } current && current != "default")
            log($"The active docker context is '{current}'; Dockyard manages the 'default' context (docker context use default)");
        return false;
    }

    public async Task Uninstall(Action<string> log)
    {
        if (File.Exists(AutostartFile)) File.Delete(AutostartFile);
        await RunSetup(log, "uninstall");
        if (Directory.Exists(Paths.SettingsDir)) Directory.Delete(Paths.SettingsDir, recursive: true);
    }

    public void ReleaseResources() { }

    async Task WriteAutostart()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
        await File.WriteAllTextAsync(AutostartFile, DesktopEntry.Autostart(Paths.Self));
    }

    async Task RunSetup(Action<string> log, params string[] args)
    {
        var script = Path.Combine(Path.GetTempPath(), $"dockyard-setup-{Guid.NewGuid():N}.sh");
        await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-linux.sh")!)
        using (var reader = new StreamReader(stream))
            await File.WriteAllTextAsync(script, (await reader.ReadToEndAsync()).Replace("\r\n", "\n"));
        try
        {
            log("Administrator step (pkexec)");
            var r = await runner.Run("pkexec", ["sh", script, .. args]);
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
        $"[Desktop Entry]\nType=Application\nName=Dockyard\nComment=Docker engine tray\nExec=\"{exe}\" --tray\nIcon=docker\nTerminal=false\nX-GNOME-Autostart-enabled=true\n";
}
