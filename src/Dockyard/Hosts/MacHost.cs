using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Paths and generated files for macOS, kept free of platform annotations so they are testable anywhere.
static class MacLayout
{
    public const string Context = "dockyard";
    public const string LaunchAgentLabel = "com.byhofman.dockyard";
    public const string CliIndex = "https://download.docker.com/mac/static/stable/aarch64/";
    public const string ComposeAsset = @"^docker-compose-darwin-aarch64$";
    public const string BuildxAsset = @"^buildx-v[\d.]+\.darwin-arm64$";

    public static string Home => Paths.UserProfile;
    public static string DataDir => Path.Combine(Home, ".dockyard");
    public static string BinDir => Path.Combine(DataDir, "bin");
    public static string DockerCli => Path.Combine(BinDir, "docker");
    public static string PluginDir => Path.Combine(Home, ".docker", "cli-plugins");
    public static string DockerSocket => Path.Combine(DataDir, "docker.sock");
    public static string ControlSocket => Path.Combine(DataDir, "run", "vm.sock");
    public static string VmConfig => Path.Combine(DataDir, "vm.json");
    public static string RootRelease => Path.Combine(DataDir, "root-release");
    public static string Helper => Path.Combine(AppContext.BaseDirectory, "dockyard-vm");
    public static string Assets => Path.Combine(AppContext.BaseDirectory, "vm");
    public static string LaunchAgent => Path.Combine(Home, "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    public static string LaunchAgentPlist(string exe) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{LaunchAgentLabel}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{Escape(exe)}</string>
                <string>--tray</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>

        """;

    // One administrator prompt: AppleScript runs the shell command with administrator privileges.
    public static string[] AdminShell(string command) =>
        ["-e", $"do shell script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges"];

    public static string PathsEntry(string binDir) => $"echo '{binDir}' > /etc/paths.d/dockyard && chmod 644 /etc/paths.d/dockyard";

    public static Version? DockerVersion(string release) => release.Split('\n')
        .Where(l => l.StartsWith("DOCKER_VERSION=", StringComparison.Ordinal))
        .Select(l => Version.TryParse(l["DOCKER_VERSION=".Length..].Trim(), out var v) ? v : null)
        .FirstOrDefault();

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

[SupportedOSPlatform("macos")]
sealed class MacHost(ICommandRunner runner) : IEngineHost
{
    public const string Engine = "linux";
    Process? vm;

    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new(Engine, "Linux", "Linux containers", "Dockyard VM (Virtualization.framework)", MacLayout.Context, "unix://" + MacLayout.DockerSocket)];

    public IReadOnlyList<ComponentInfo> Components { get; } =
    [
        new("LinuxEngine", "Docker Engine (Linux VM)"),
        new("Cli", "Docker CLI"),
        new("Compose", "Docker Compose"),
        new("Buildx", "Docker Buildx"),
        new("App", "Dockyard"),
    ];

    public bool IsInstalled => File.Exists(MacLayout.VmConfig) && File.Exists(MacLayout.DockerCli);

    public Task<bool> IsRunning(string engine) => Task.Run(Ping);

    public async Task Start(string engine)
    {
        if (vm is { HasExited: false } && Ping()) return;
        if (vm is null || vm.HasExited)
        {
            vm?.Dispose();
            if (!File.Exists(MacLayout.VmConfig)) throw new InvalidOperationException("Dockyard is not set up yet; run the installer first");
            vm = runner.Spawn(MacLayout.Helper, "run", "--config", MacLayout.VmConfig);
        }
        var process = vm;
        await Wait.For(Ping, TimeSpan.FromSeconds(60), "Linux engine", failed: () => process.HasExited);
    }

    public async Task Stop(string engine)
    {
        if (File.Exists(MacLayout.ControlSocket)) await SendControl("stop");
        if (vm is { } process)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(); }
            process.Dispose();
            vm = null;
        }
    }

    public string ActiveEngine() => Engine;

    public Task UseContext(string engine) => runner.Check(MacLayout.DockerCli, "context", "use", MacLayout.Context);

    public async Task<Version?> Installed(string component) => component switch
    {
        "LinuxEngine" => File.Exists(MacLayout.RootRelease) ? MacLayout.DockerVersion(await File.ReadAllTextAsync(MacLayout.RootRelease)) : null,
        "Cli" => File.Exists(MacLayout.DockerCli) ? Updates.ParseVersion((await runner.Run(MacLayout.DockerCli, "--version")).Output) : null,
        "Compose" => await Plugin("docker-compose", "version", "--short"),
        "Buildx" => await Plugin("docker-buildx", "version"),
        "App" => Updates.ParseVersion(Paths.AppVersion),
        _ => null,
    };

    public async Task<Release?> Latest(string component) => component switch
    {
        "LinuxEngine" => File.Exists(Path.Combine(MacLayout.Assets, "dockyard-release")) && MacLayout.DockerVersion(
            await File.ReadAllTextAsync(Path.Combine(MacLayout.Assets, "dockyard-release"))) is { } bundled
            ? new Release(bundled, bundled.ToString(), null) : null,
        "Cli" => await Updates.LatestStatic(MacLayout.CliIndex, "tgz"),
        "Compose" => await Updates.LatestGitHub("docker/compose", MacLayout.ComposeAsset),
        "Buildx" => await Updates.LatestGitHub("docker/buildx", MacLayout.BuildxAsset),
        "App" => await Updates.LatestApp(),
        _ => null,
    };

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        foreach (var (component, release) in components)
        {
            switch (component)
            {
                case "Cli" when release is not null: await InstallCli(log, release); break;
                case "Compose" when release is not null: await InstallPlugin(log, "docker-compose", release); break;
                case "Buildx" when release is not null: await InstallPlugin(log, "docker-buildx", release); break;
                case "LinuxEngine":
                    var running = Ping();
                    if (running) await Stop(Engine);
                    await Prepare(log);
                    if (running) await Start(Engine);
                    break;
                case "App":
                    log("Update Dockyard by running the install script again (curl -fsSL …/install-mac.sh | sh)");
                    break;
            }
        }
        return false;
    }

    public async Task<bool> Install(Action<string> log)
    {
        if ((await runner.Run("/usr/bin/arch", "-x86_64", "/usr/bin/true")).ExitCode != 0)
            log("Rosetta is not installed, so amd64 images will not run: softwareupdate --install-rosetta --agree-to-license");

        await InstallCli(log, await Updates.LatestStatic(MacLayout.CliIndex, "tgz"));
        await InstallPlugin(log, "docker-compose", await Updates.LatestGitHub("docker/compose", MacLayout.ComposeAsset));
        await InstallPlugin(log, "docker-buildx", await Updates.LatestGitHub("docker/buildx", MacLayout.BuildxAsset));

        if (!File.Exists("/etc/paths.d/dockyard"))
        {
            log("Administrator step: adding ~/.dockyard/bin to the PATH (/etc/paths.d/dockyard)");
            var r = await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(MacLayout.PathsEntry(MacLayout.BinDir)));
            if (r.ExitCode != 0) log("PATH entry skipped (approval declined); add ~/.dockyard/bin to your PATH yourself");
        }

        await Prepare(log);

        var host = "unix://" + MacLayout.DockerSocket;
        var exists = (await runner.Run(MacLayout.DockerCli, "context", "inspect", MacLayout.Context)).ExitCode == 0;
        await runner.Check(MacLayout.DockerCli, "context", exists ? "update" : "create", MacLayout.Context, "--docker", $"host={host}");
        await runner.Check(MacLayout.DockerCli, "context", "use", MacLayout.Context);
        log($"Docker context '{MacLayout.Context}' → {host}");

        Directory.CreateDirectory(Path.GetDirectoryName(MacLayout.LaunchAgent)!);
        await File.WriteAllTextAsync(MacLayout.LaunchAgent, MacLayout.LaunchAgentPlist(Paths.Self));
        log($"{Paths.AppName} starts at login ({MacLayout.LaunchAgent})");
        return false;
    }

    public async Task Uninstall(Action<string> log)
    {
        await Stop(Engine);
        if (File.Exists(MacLayout.LaunchAgent)) File.Delete(MacLayout.LaunchAgent);
        if (File.Exists(MacLayout.DockerCli))
        {
            await runner.Run(MacLayout.DockerCli, "context", "use", "default");
            await runner.Run(MacLayout.DockerCli, "context", "rm", "-f", MacLayout.Context);
        }
        if (File.Exists("/etc/paths.d/dockyard"))
            await runner.Run("/usr/bin/osascript", MacLayout.AdminShell("rm -f /etc/paths.d/dockyard"));
        if (Directory.Exists(MacLayout.BinDir)) Directory.Delete(MacLayout.BinDir, recursive: true);
        foreach (var plugin in new[] { "docker-compose", "docker-buildx" })
        {
            var path = Path.Combine(MacLayout.PluginDir, plugin);
            if (File.Exists(path)) File.Delete(path);
        }
        log($"Your images and containers were kept in {Path.Combine(MacLayout.DataDir, "data.img")}; delete ~/.dockyard to remove them");
    }

    public void ReleaseResources()
    {
        if (vm is { HasExited: false })
        {
            SendControl("stop").Wait(TimeSpan.FromSeconds(2));
            if (!vm.WaitForExit(TimeSpan.FromSeconds(25))) vm.Kill();
        }
        vm?.Dispose();
        vm = null;
    }

    async Task Prepare(Action<string> log)
    {
        var r = await runner.Run(MacLayout.Helper, "prepare", "--assets", MacLayout.Assets, "--config", MacLayout.VmConfig);
        foreach (var line in (r.Output + r.Error).Split('\n', StringSplitOptions.RemoveEmptyEntries)) log("  " + line.Trim());
        if (r.ExitCode != 0) throw new InvalidOperationException($"Preparing the VM failed ({r.ExitCode})");
    }

    async Task InstallCli(Action<string> log, Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tgz = Path.Combine(dir, "docker.tgz");
            await Updates.DownloadFile(release.DownloadUrl!, tgz);
            await runner.Check("/usr/bin/tar", "-xzf", tgz, "-C", dir, "docker/docker");
            Directory.CreateDirectory(MacLayout.BinDir);
            File.Copy(Path.Combine(dir, "docker", "docker"), MacLayout.DockerCli, overwrite: true);
            File.SetUnixFileMode(MacLayout.DockerCli, (UnixFileMode)0b111_101_101);
            log($"docker CLI {release.Version} in {MacLayout.BinDir}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    async Task InstallPlugin(Action<string> log, string name, Release release)
    {
        Directory.CreateDirectory(MacLayout.PluginDir);
        var target = Path.Combine(MacLayout.PluginDir, name);
        await Updates.DownloadFile(release.DownloadUrl!, target);
        File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
        log($"{name} {release.Tag}");
    }

    async Task<Version?> Plugin(string name, params string[] args)
    {
        var path = Path.Combine(MacLayout.PluginDir, name);
        return File.Exists(path) ? Updates.ParseVersion((await runner.Run(path, args)).Output) : null;
    }

    static async Task SendControl(string command)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacLayout.ControlSocket));
            await socket.SendAsync(Encoding.ASCII.GetBytes(command + "\n"));
        }
        catch (SocketException) { }
    }

    static bool Ping()
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.ReceiveTimeout = 1000;
            socket.Connect(new UnixDomainSocketEndPoint(MacLayout.DockerSocket));
            socket.Send("GET /_ping HTTP/1.0\r\nHost: docker\r\n\r\n"u8);
            var buffer = new byte[256];
            var n = socket.Receive(buffer);
            return Encoding.ASCII.GetString(buffer, 0, n).StartsWith("HTTP/1.0 200", StringComparison.Ordinal)
                || Encoding.ASCII.GetString(buffer, 0, n).StartsWith("HTTP/1.1 200", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
