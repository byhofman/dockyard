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
    public static string UserApplications => Path.Combine(Home, "Applications");

    // Where the app belongs: where it already is when that is an Applications folder, otherwise ~/Applications.
    public static string InstallLocation(string baseDirectory, string userApplications)
    {
        var bundle = Bundle(baseDirectory);
        var folder = bundle is null ? null : Path.GetDirectoryName(bundle);
        return folder == "/Applications" || folder == userApplications ? bundle! : Path.Combine(userApplications, "Dockyard.app");
    }

    // The files of the app that is or will be installed. A folder that is no bundle (a development build) is its own install.
    public static string AppBase => Bundle(AppContext.BaseDirectory) is null
        ? AppContext.BaseDirectory
        : Path.Combine(InstallLocation(AppContext.BaseDirectory, UserApplications), "Contents", "MacOS");

    public static string InstalledExe => Path.Combine(AppBase, "Dockyard");
    public static string Helper => Path.Combine(AppBase, "dockyard-vm");
    public static string Assets => Directory.Exists(Path.Combine(AppBase, "..", "Resources", "vm"))
        ? Path.GetFullPath(Path.Combine(AppBase, "..", "Resources", "vm"))
        : Path.Combine(AppBase, "vm");
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

    // Everything of the dashboard in the bundle: the tray links none of the dylibs, the engine-only edition leaves them out too.
    public static IEnumerable<string> DashboardFiles(string macosDir) =>
        !Directory.Exists(macosDir) ? [] : Directory.GetFiles(macosDir).Where(f => Path.GetFileName(f) == "Dockyard.Dashboard" || f.EndsWith(".dylib", StringComparison.Ordinal));

    public static Version? BundleVersion(string infoPlist)
    {
        var m = Regex.Match(infoPlist, @"<key>CFBundleShortVersionString</key>\s*<string>([^<]+)</string>");
        return m.Success ? Updates.ParseVersion(m.Groups[1].Value) : null;
    }

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
        "App" => InstalledAppVersion(),
        _ => null,
    };

    // The wizard runs from a download and can be newer than what is installed.
    static Version? InstalledAppVersion()
    {
        var plist = Path.Combine(MacLayout.AppBase, "..", "Info.plist");
        return File.Exists(plist) ? MacLayout.BundleVersion(File.ReadAllText(plist)) : Updates.ParseVersion(Paths.AppVersion);
    }

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

    public bool RunsFromInstallLocation => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == Path.TrimEndingDirectorySeparator(MacLayout.AppBase);

    public bool HasDashboard => File.Exists(Path.Combine(MacLayout.AppBase, "Dockyard.Dashboard"));

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        if (installed) await AddDashboard(log);
        else await RemoveDashboard(log);
        return false;
    }

    // From this bundle when it has the dashboard (the wizard), otherwise from the release of this version.
    async Task AddDashboard(Action<string> log)
    {
        string? downloaded = null;
        try
        {
            var source = AppContext.BaseDirectory;
            if (!File.Exists(Path.Combine(source, "Dockyard.Dashboard")))
            {
                log("Downloading the dashboard");
                downloaded = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
                var zip = await Updates.DownloadReleaseAsset(MacLayout.ZipAsset(engineOnly: false), downloaded, isPattern: true);
                await runner.Check("/usr/bin/ditto", "-x", "-k", zip, downloaded);
                source = Path.Combine(downloaded, "Dockyard.app", "Contents", "MacOS");
            }
            foreach (var file in MacLayout.DashboardFiles(source))
            {
                var target = Path.Combine(MacLayout.AppBase, Path.GetFileName(file));
                // A new inode: macOS kills a process whose signed file was overwritten in place.
                File.Delete(target);
                File.Copy(file, target);
            }
            await SealBundle();
            log("Added the dashboard");
        }
        finally
        {
            if (downloaded is not null) Directory.Delete(downloaded, recursive: true);
        }
    }

    async Task RemoveDashboard(Action<string> log)
    {
        foreach (var file in MacLayout.DashboardFiles(MacLayout.AppBase)) File.Delete(file);
        await SealBundle();
        log("Removed the dashboard");
    }

    // Only the bundle's own seal changes: signing it without --deep leaves dockyard-vm and its entitlements alone.
    async Task SealBundle()
    {
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle)
            await runner.Run("/usr/bin/codesign", "--force", "--sign", "-", bundle);
    }

    // Downloads the release zip (no quarantine attribute, so Gatekeeper does not block it) and swaps the
    // app bundle once this process has exited.
    async Task SelfUpdate(Action<string> log, bool engineOnly, string? tag)
    {
        var bundle = MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar)
            ?? throw new InvalidOperationException("Dockyard is not running from Dockyard.app; run install-mac.sh again");
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-update-{Guid.NewGuid():N}");
        var zip = await Updates.DownloadReleaseAsset(MacLayout.ZipAsset(engineOnly), dir, isPattern: true, tag);
        await runner.Check("/usr/bin/ditto", "-x", "-k", zip, dir);
        var script = Path.Combine(dir, "swap.sh");
        await File.WriteAllTextAsync(script, MacLayout.SwapScript(Environment.ProcessId, Path.Combine(dir, "Dockyard.app"), bundle));
        Process.Start(new ProcessStartInfo("/bin/sh", [script]) { UseShellExecute = false })?.Dispose();
        log($"Replacing {bundle} and restarting");
    }

    public async Task<bool> Install(Action<string> log, InstallRequest request)
    {
        await InstallBundle(log, request.Dashboard);

        await InstallCli(log, await Updates.LatestStatic(MacLayout.CliIndex, "tgz"));
        await InstallPlugin(log, "docker-compose", await Updates.LatestGitHub("docker/compose", MacLayout.ComposeAsset));
        await InstallPlugin(log, "docker-buildx", await Updates.LatestGitHub("docker/buildx", MacLayout.BuildxAsset));

        await AdministratorSteps(log, request.Silent);

        await PrepareForInstall(log);

        var host = "unix://" + MacLayout.DockerSocket;
        var exists = (await runner.Run(MacLayout.DockerCli, "context", "inspect", MacLayout.Context)).ExitCode == 0;
        await runner.Check(MacLayout.DockerCli, "context", exists ? "update" : "create", MacLayout.Context, "--docker", $"host={host}");
        var current = (await runner.Run(MacLayout.DockerCli, "context", "show")).Output.Trim();
        if (current.Length > 0 && current != MacLayout.Context) await File.WriteAllTextAsync(MacLayout.PreviousContextFile, current);
        await runner.Check(MacLayout.DockerCli, "context", "use", MacLayout.Context);
        log($"Docker context '{MacLayout.Context}' → {host}");

        Directory.CreateDirectory(Path.GetDirectoryName(MacLayout.LaunchAgent)!);
        await File.WriteAllTextAsync(MacLayout.LaunchAgent, MacLayout.LaunchAgentPlist(MacLayout.InstalledExe));
        log($"{Paths.AppName} starts at login ({MacLayout.LaunchAgent})");
        return false;
    }

    public void StartApp()
    {
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle) Shell.Spawn("/usr/bin/open", bundle, "--args", "--tray").Dispose();
        else Shell.Spawn(MacLayout.InstalledExe, "--tray").Dispose();
    }

    public string DoneMessage(bool reboot) => "Done. Dockyard runs in the menu bar.";

    // Copies the wizard's bundle to where the app belongs, from a download, a disk image or a translocated copy.
    // The login item and vm.json point at it, so it has to stay there. Nothing under ~/.dockyard is touched.
    async Task InstallBundle(Action<string> log, bool dashboard)
    {
        if (RunsFromInstallLocation)
        {
            if (HasDashboard && !dashboard) await RemoveDashboard(log);
            else if (!HasDashboard && dashboard) await AddDashboard(log);
            return;
        }

        var source = MacLayout.Bundle(AppContext.BaseDirectory)!;
        var target = MacLayout.InstallLocation(AppContext.BaseDirectory, MacLayout.UserApplications);
        var incoming = target + ".new";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
        await runner.Check("/usr/bin/ditto", source, incoming);
        await runner.Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", incoming);
        // Only that install's tray: its VM keeps running, and a Dockyard somewhere else is none of this setup's business.
        await runner.Run("/usr/bin/pkill", "-f", Path.Combine(target, "Contents", "MacOS", "Dockyard"));
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.Move(incoming, target);
        log($"Installed {target}");
        if (!dashboard) await RemoveDashboard(log);
    }

    // Rosetta and the PATH entry need an administrator. A silent setup shows no dialog: then it needs passwordless sudo,
    // and without that the rest of Dockyard still works and the log says what is left.
    async Task AdministratorSteps(Action<string> log, bool silent)
    {
        List<string> steps = [];
        var rosetta = (await runner.Run("/usr/bin/arch", "-x86_64", "/usr/bin/true")).ExitCode == 0;
        if (!rosetta) steps.Add("/usr/sbin/softwareupdate --install-rosetta --agree-to-license");
        if (!File.Exists("/etc/paths.d/dockyard")) steps.Add(MacLayout.PathsEntry(MacLayout.BinDir));
        if (steps.Count == 0) return;

        var script = string.Join(" ; ", steps);
        log("Administrator step: " + (rosetta ? "adding ~/.dockyard/bin to the PATH" : "installing Rosetta for amd64 images and adding ~/.dockyard/bin to the PATH"));
        var r = silent
            ? await runner.Run("sudo", "-n", "/bin/sh", "-c", script)
            : await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script));
        if (r.ExitCode != 0)
            log($"Skipped ({(silent ? "no passwordless sudo" : "approval declined")}). {MacLayout.BinDir} is not on the PATH until the wizard runs with an administrator's approval" + (rosetta ? "." : ", and amd64 images need Rosetta."));
    }

    // A VM running on the previous image has to stop for the new one; with the same image it can keep running.
    async Task PrepareForInstall(Action<string> log)
    {
        var bundled = Path.Combine(MacLayout.Assets, "dockyard-release");
        var changed = File.Exists(MacLayout.RootRelease) && File.Exists(bundled) && await File.ReadAllTextAsync(MacLayout.RootRelease) != await File.ReadAllTextAsync(bundled);
        var restart = changed && ping();
        if (restart) await Stop(Engine);
        await Prepare(log);
        if (restart) await Start(Engine);
    }

    public async Task Uninstall(Action<string> log, bool removeData)
    {
        await Stop(Engine);
        if (!RunsFromInstallLocation) await runner.Run("/usr/bin/pkill", "-f", MacLayout.InstalledExe);
        if (File.Exists(MacLayout.LaunchAgent)) File.Delete(MacLayout.LaunchAgent);
        if (File.Exists(MacLayout.DockerCli))
        {
            var previous = File.Exists(MacLayout.PreviousContextFile) ? await File.ReadAllTextAsync(MacLayout.PreviousContextFile) : null;
            await runner.Run(MacLayout.DockerCli, "context", "use", MacLayout.ContextToRestore(previous));
            await runner.Run(MacLayout.DockerCli, "context", "rm", "-f", MacLayout.Context);
        }
        if (await IsOurPathEntry())
            await runner.Run("/usr/bin/osascript", MacLayout.AdminShell("rm -f /etc/paths.d/dockyard"));
        if (Directory.Exists(MacLayout.BinDir)) Directory.Delete(MacLayout.BinDir, recursive: true);
        foreach (var plugin in new[] { "docker-compose", "docker-buildx" })
            MacLayout.RemovePlugin(Path.Combine(MacLayout.PluginDir, plugin), MacLayout.PluginLinksDir);
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle && Directory.Exists(bundle)) Directory.Delete(bundle, recursive: true);
        if (removeData)
        {
            if (Directory.Exists(MacLayout.DataDir)) Directory.Delete(MacLayout.DataDir, recursive: true);
            log("Removed your images and containers");
        }
        else log($"Your images and containers were kept in {Path.Combine(MacLayout.DataDir, "data.img")}");
    }

    static async Task<bool> IsOurPathEntry() =>
        File.Exists("/etc/paths.d/dockyard") && (await File.ReadAllTextAsync("/etc/paths.d/dockyard")).Trim() == MacLayout.BinDir;

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
