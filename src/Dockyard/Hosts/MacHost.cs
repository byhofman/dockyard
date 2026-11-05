using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
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
    public static string PluginLinksDir => Path.Combine(DataDir, "plugin-links");
    public static string PreviousContextFile => Path.Combine(DataDir, "previous-context");
    public static string RootRelease => Path.Combine(DataDir, "root-release");
    public static string Helper => Path.Combine(AppContext.BaseDirectory, "dockyard-vm");
    public static string Assets => Directory.Exists(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "vm"))
        ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "vm"))
        : Path.Combine(AppContext.BaseDirectory, "vm");
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

        """.ReplaceLineEndings("\n");

    // One administrator prompt: AppleScript runs the shell command with administrator privileges.
    public static string[] AdminShell(string command) =>
        ["-e", $"do shell script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" with administrator privileges"];

    public static string PathsEntry(string binDir) => $"echo '{binDir}' > /etc/paths.d/dockyard && chmod 644 /etc/paths.d/dockyard";

    public static string ZipAsset(bool engineOnly) => engineOnly ? @"^Dockyard-[\d.]+-macos-engine-only\.zip$" : @"^Dockyard-[\d.]+-macos\.zip$";

    // A disk image goes away when ejected and a translocated copy on restart, but the login item and vm.json would keep pointing there.
    public static bool IsTemporaryLocation(string baseDirectory) =>
        Regex.IsMatch(baseDirectory, @"^/Volumes/Dockyard( \d+)?/") || baseDirectory.Contains("/AppTranslocation/", StringComparison.Ordinal);

    // Another Docker app's plugin is usually a symlink into its bundle; it is put back on uninstall.
    public static void SetAsideLinkedPlugin(string plugin, string linksDir)
    {
        if (new FileInfo(plugin).LinkTarget is null) return;
        Directory.CreateDirectory(linksDir);
        var kept = Path.Combine(linksDir, Path.GetFileName(plugin));
        if (new FileInfo(kept).LinkTarget is null) File.Move(plugin, kept);
        else File.Delete(plugin);
    }

    public static void RemovePlugin(string plugin, string linksDir)
    {
        if (!Directory.Exists(Path.GetDirectoryName(plugin))) return;
        File.Delete(plugin);
        var kept = Path.Combine(linksDir, Path.GetFileName(plugin));
        if (new FileInfo(kept).LinkTarget is not null) File.Move(kept, plugin);
    }

    public static string ContextToRestore(string? saved) => string.IsNullOrWhiteSpace(saved) ? "default" : saved.Trim();

    public static string? Bundle(string baseDirectory)
    {
        var contents = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDirectory));
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal) ? bundle : null;
    }

    public static string SwapScript(int pid, string newBundle, string bundle) => $"""
        #!/bin/sh
        while kill -0 {pid} 2>/dev/null; do sleep 0.2; done
        rm -rf '{bundle}.old'
        mv '{bundle}' '{bundle}.old' && mv '{newBundle}' '{bundle}' && rm -rf '{bundle}.old'
        open '{bundle}' --args --tray

        """.ReplaceLineEndings("\n");

    public static Version? DockerVersion(string release) => release.Split('\n')
        .Where(l => l.StartsWith("DOCKER_VERSION=", StringComparison.Ordinal))
        .Select(l => Version.TryParse(l["DOCKER_VERSION=".Length..].Trim(), out var v) ? v : null)
        .FirstOrDefault();

    static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

[SupportedOSPlatform("macos")]
sealed class MacHost(ICommandRunner runner, Func<bool>? ping = null) : IEngineHost
{
    public const string Engine = "linux";
    readonly Func<bool> ping = ping ?? Ping;
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

    public Task<bool> IsRunning(string engine) => Task.Run(ping);

    public async Task Start(string engine)
    {
        // The engine can outlive the tray that started it; a second helper would only truncate its console log and fail.
        if (await Task.Run(ping)) return;
        if (vm is null || vm.HasExited)
        {
            vm?.Dispose();
            if (!File.Exists(MacLayout.VmConfig)) throw new InvalidOperationException("Dockyard is not set up yet; run the installer first");
            // Keeps vm.json on this app's kernel after the app moved, and the root image's modules matching that kernel after an update.
            await Prepare(_ => { });
            vm = runner.Spawn(MacLayout.Helper, "run", "--config", MacLayout.VmConfig);
        }
        var process = vm;
        await Wait.For(ping, TimeSpan.FromSeconds(60), "Linux engine", failed: () => process.HasExited);
    }

    public async Task Stop(string engine)
    {
        var asked = File.Exists(MacLayout.ControlSocket) && await SendControl("stop");
        // A helper this process did not start still holds the disk images until it exits, and a new VM cannot attach them before that.
        if (asked && vm is null)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (File.Exists(MacLayout.ControlSocket) && DateTime.UtcNow < until) await Task.Delay(200);
        }
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
        "App" => await Updates.LatestApp(MacLayout.ZipAsset(!HasDashboard)),
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
                    var running = ping();
                    if (running) await Stop(Engine);
                    await Prepare(log);
                    if (running) await Start(Engine);
                    break;
                case "App":
                    await SelfUpdate(log, engineOnly: !HasDashboard, release?.Tag);
                    return true;
            }
        }
        return false;
    }

    public bool HasDashboard => File.Exists(Path.Combine(AppContext.BaseDirectory, "Dockyard.Dashboard"));

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        if (installed)
        {
            await SelfUpdate(log, engineOnly: false, tag: null);
            return true;
        }
        var dashboard = Path.Combine(AppContext.BaseDirectory, "Dockyard.Dashboard");
        if (File.Exists(dashboard)) File.Delete(dashboard);
        log("Removed the dashboard");
        return false;
    }

    // Downloads the release zip (no quarantine attribute, so Gatekeeper does not block it) and swaps the
    // app bundle once this process has exited.
    async Task SelfUpdate(Action<string> log, bool engineOnly, string? tag)
    {
        var bundle = MacLayout.Bundle(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("Dockyard is not running from Dockyard.app; run install-mac.sh again");
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-update-{Guid.NewGuid():N}");
        var zip = await Updates.DownloadReleaseAsset(MacLayout.ZipAsset(engineOnly), dir, isPattern: true, tag);
        await runner.Check("/usr/bin/ditto", "-x", "-k", zip, dir);
        var script = Path.Combine(dir, "swap.sh");
        await File.WriteAllTextAsync(script, MacLayout.SwapScript(Environment.ProcessId, Path.Combine(dir, "Dockyard.app"), bundle));
        Process.Start(new ProcessStartInfo("/bin/sh", [script]) { UseShellExecute = false })?.Dispose();
        log($"Replacing {bundle} and restarting");
    }

    public async Task<bool> Install(Action<string> log, bool engineOnly)
    {
        if (MacLayout.IsTemporaryLocation(AppContext.BaseDirectory))
            throw new InvalidOperationException("Move Dockyard.app to Applications and open it from there");

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
        var current = (await runner.Run(MacLayout.DockerCli, "context", "show")).Output.Trim();
        if (current.Length > 0 && current != MacLayout.Context) await File.WriteAllTextAsync(MacLayout.PreviousContextFile, current);
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
            var previous = File.Exists(MacLayout.PreviousContextFile) ? await File.ReadAllTextAsync(MacLayout.PreviousContextFile) : null;
            await runner.Run(MacLayout.DockerCli, "context", "use", MacLayout.ContextToRestore(previous));
            await runner.Run(MacLayout.DockerCli, "context", "rm", "-f", MacLayout.Context);
        }
        if (File.Exists("/etc/paths.d/dockyard"))
            await runner.Run("/usr/bin/osascript", MacLayout.AdminShell("rm -f /etc/paths.d/dockyard"));
        if (Directory.Exists(MacLayout.BinDir)) Directory.Delete(MacLayout.BinDir, recursive: true);
        foreach (var plugin in new[] { "docker-compose", "docker-buildx" })
            MacLayout.RemovePlugin(Path.Combine(MacLayout.PluginDir, plugin), MacLayout.PluginLinksDir);
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
        MacLayout.SetAsideLinkedPlugin(target, MacLayout.PluginLinksDir);
        await Updates.DownloadFile(release.DownloadUrl!, target);
        File.SetUnixFileMode(target, (UnixFileMode)0b111_101_101);
        log($"{name} {release.Tag}");
    }

    async Task<Version?> Plugin(string name, params string[] args)
    {
        var path = Path.Combine(MacLayout.PluginDir, name);
        return File.Exists(path) ? Updates.ParseVersion((await runner.Run(path, args)).Output) : null;
    }

    static async Task<bool> SendControl(string command)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(MacLayout.ControlSocket));
            await socket.SendAsync(Encoding.ASCII.GetBytes(command + "\n"));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
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
