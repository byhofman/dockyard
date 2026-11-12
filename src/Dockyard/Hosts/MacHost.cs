using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Dockyard.Core;

namespace Dockyard.Hosts;

[SupportedOSPlatform("macos")]
sealed class MacHost(ICommandRunner runner, Func<bool>? ping = null, Func<bool>? helperAnswers = null) : IEngineHost
{
    public const string Engine = "linux";
    static readonly TimeSpan HelperPatience = TimeSpan.FromSeconds(60);
    readonly Func<bool> ping = ping ?? Ping;
    readonly Func<bool> helperAnswers = helperAnswers ?? HelperAnswers;
    Process? vm;

    public IReadOnlyList<EngineInfo> Engines { get; } =
        [new(Engine, "Linux", "Linux containers", "Dockyard VM (Virtualization.framework)", MacLayout.Context, "unix://" + MacLayout.DockerSocket)];

    public IReadOnlyList<ComponentInfo> Components => Catalog;

    public static IReadOnlyList<ComponentInfo> Catalog { get; } =
    [
        new("LinuxEngine", "Docker Engine (Linux VM)", RequiredBecause: "it runs your containers, and it is the only engine here. Uninstall removes it.", RequiredFlag: "linux-engine"),
        new("Cli", "Docker CLI", RequiredBecause: "the engine, Compose and Buildx are used through it.", RequiredFlag: "cli"),
        new("Compose", "Docker Compose", Flag: "compose", Note: "Needs the Docker CLI. The dashboard's Compose actions use it."),
        new("Buildx", "Docker Buildx", Flag: "buildx", Note: "Needs the Docker CLI. docker build uses it."),
        Kind.Component,
        new("App", "Dockyard", RequiredBecause: "it is what runs everything here. Uninstall removes it."),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard", Note: "Optional. Opens from the menu bar."),
    ];

    public bool IsInstalled => File.Exists(MacLayout.VmConfig) && File.Exists(MacLayout.DockerCli);

    public Task<bool> IsRunning(string engine) => Task.Run(ping);

    public async Task Start(string engine)
    {
        for (var attempt = 0; ; attempt++)
        {
            // The engine can outlive the tray that started it; a second helper would only truncate its console log and fail.
            if (await Task.Run(ping)) return;
            // A helper that answers while its engine does not is starting or stopping, and holds the disk images either way.
            var until = DateTime.UtcNow + HelperPatience;
            while (await Task.Run(helperAnswers) && DateTime.UtcNow < until)
            {
                if (await Task.Run(ping)) return;
                await Task.Delay(200);
            }
            if (vm is null || vm.HasExited)
            {
                vm?.Dispose();
                if (!File.Exists(MacLayout.VmConfig)) throw new InvalidOperationException("Dockyard is not set up yet; run the installer first");
                // Keeps vm.json on this app's kernel after the app moved, and the root image's modules matching that kernel after an update.
                await Prepare(_ => { });
                await WriteEngineConfig();
                vm = runner.Spawn(MacLayout.Helper, "run", "--config", MacLayout.VmConfig);
            }
            var process = vm;
            try
            {
                await Wait.For(ping, HelperPatience, "Linux engine", failed: () => process.HasExited);
                return;
            }
            // A stop from elsewhere can arrive while this start waits, which ends the helper it was waiting for.
            catch (InvalidOperationException) when (attempt == 0) { }
        }
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
        Kind.Id => File.Exists(MacLayout.KindCli) ? Updates.ParseVersion((await runner.Run(MacLayout.KindCli, "version")).Output) : null,
        "App" => InstalledAppVersion(),
        ComponentRules.DashboardId => await InstalledDashboardVersion(),
        _ => null,
    };

    // Asking the old binary would open its window, since it has no --version: it came with the app's version.
    async Task<Version?> InstalledDashboardVersion()
    {
        var current = Path.Combine(MacLayout.AppBase, MacLayout.DashboardNames[0]);
        if (File.Exists(current)) return Updates.ParseVersion((await runner.Run(current, "--version")).Output);
        return HasDashboard ? InstalledAppVersion() : null;
    }

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
        "Cli" => MacLayout.Cli,
        "Compose" => MacLayout.Compose,
        "Buildx" => MacLayout.Buildx,
        Kind.Id => Kind.Release,
        "App" => await Updates.LatestApp(MacLayout.ZipAsset(!HasDashboard)),
        ComponentRules.DashboardId => await Updates.LatestApp(MacLayout.ZipAsset(engineOnly: false)),
        _ => null,
    };

    public string CaDirectory => Path.Combine(MacLayout.DataDir, "ca");
    public ICaKeyStore CaKeys => new KeychainCaKey(MacLayout.LoginKeychain);

    // A window: the login keychain, for which macOS asks the user's own approval. Silent: only what sudo allows unasked.
    public async Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent)
    {
        var r = silent
            ? await runner.Run("sudo", "-n", "/usr/bin/security", "add-trusted-cert", "-d", "-r", "trustRoot", "-p", "ssl", "-k", "/Library/Keychains/System.keychain", certificateFile)
            : await runner.Run("/usr/bin/security", "add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", MacLayout.LoginKeychain, certificateFile);
        if (r.ExitCode != 0) return false;
        log("This Mac trusts Dockyard's certificate, for names under dockyard.test only");
        return true;
    }

    public async Task UntrustCa(Action<string> log, string certificateFile)
    {
        if (!File.Exists(certificateFile)) return;
        var thumbprint = LocalCa.Thumbprint(await File.ReadAllTextAsync(certificateFile));
        // The trust setting goes first, while the certificate it names is still there to name. Asking to remove what is not
        // there can still raise an approval dialog, so each domain is looked up first. This user's setting asks this user,
        // as it did when it was made from the window. The system's is rewritten as root: remove-trusted-cert waits for an
        // approval dialog there even as root and without a screen, which hung every headless uninstall.
        if (MacLayout.HasTrustSetting((await runner.Run("/usr/bin/security", "dump-trust-settings")).Output))
            await runner.Run("/usr/bin/security", "remove-trusted-cert", certificateFile);
        var systemTrust = MacLayout.HasTrustSetting((await runner.Run("/usr/bin/security", "dump-trust-settings", "-d")).Output);
        if ((await runner.Run("/usr/bin/security", "find-certificate", "-a", "-Z", "-c", LocalCa.SubjectName, MacLayout.LoginKeychain)).Output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase))
        {
            await runner.Run("/usr/bin/security", "delete-certificate", "-Z", thumbprint, MacLayout.LoginKeychain);
        }
        // In the system keychain only after a silent setup; a prompt for what is not there would make no sense.
        var inSystem = (await runner.Run("/usr/bin/security", "find-certificate", "-a", "-Z", "-c", LocalCa.SubjectName, "/Library/Keychains/System.keychain")).Output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
        if (inSystem || systemTrust)
        {
            var script = string.Join(" ; ", new[]
            {
                systemTrust ? MacLayout.RemoveSystemTrustSetting(thumbprint) : null,
                inSystem ? $"security delete-certificate -Z {thumbprint} /Library/Keychains/System.keychain" : null,
            }.OfType<string>());
            if ((await runner.Run("sudo", "-n", "/bin/sh", "-c", script)).ExitCode != 0)
                await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script));
        }
        foreach (var keychain in new[] { MacLayout.LoginKeychain, "/Library/Keychains/System.keychain" })
            if ((await runner.Run("/usr/bin/security", "find-certificate", "-a", "-Z", "-c", LocalCa.SubjectName, keychain)).Output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Dockyard's certificate is still in {keychain}. Nothing was removed, so this can be tried again.");
        if (MacLayout.HasTrustSetting((await runner.Run("/usr/bin/security", "dump-trust-settings", "-d")).Output))
            log("This Mac's trust settings still name a Dockyard certificate; Keychain Access can remove it");
        log("Dockyard's certificate is out of this Mac's trust store");
    }

    public bool HasEngineConfiguration => true;
    public string? OwnSocket => MacLayout.DockerSocket;

    public async Task ApplyEngineConfiguration(Action<string> log)
    {
        await WriteEngineConfig(log);
        if (ping()) { await Stop(Engine); await Start(Engine); }
    }

    public async Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components)
    {
        foreach (var (component, release) in components)
        {
            switch (component)
            {
                case "Cli" when release is not null: await InstallCli(log, release); break;
                case "Compose" when release is not null: await InstallPlugin(log, "docker-compose", release); break;
                case "Buildx" when release is not null: await InstallPlugin(log, "docker-buildx", release); break;
                case Kind.Id: await InstallKind(log); break;
                case ComponentRules.DashboardId when release is not null: await AddDashboard(log, release.Tag); break;
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

    public bool HasDashboard => MacLayout.HasDashboardIn(MacLayout.AppBase);

    public async Task<bool> SetDashboard(Action<string> log, bool installed)
    {
        if (installed) await AddDashboard(log);
        else await RemoveDashboard(log);
        return false;
    }

    public async Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false)
    {
        var plugin = component switch { "Compose" => "docker-compose", "Buildx" => "docker-buildx", _ => null };
        if (component == ComponentRules.DashboardId) return await SetDashboard(log, installed);
        if (component == Kind.Id)
        {
            if (installed) await InstallKind(log);
            else RemoveKind(log);
            return false;
        }
        if (plugin is null) throw new InvalidOperationException($"{component} cannot be changed here");
        if (!installed)
        {
            MacLayout.RemovePlugin(Path.Combine(MacLayout.PluginDir, plugin), MacLayout.PluginLinksDir);
            log($"Removed {plugin}");
        }
        else await InstallPlugin(log, plugin, (await Latest(component))!);
        return false;
    }

    // From this bundle when it has the dashboard (the wizard), otherwise from the release of this version, or the one named.
    async Task AddDashboard(Action<string> log, string? tag = null)
    {
        string? downloaded = null;
        try
        {
            var source = AppContext.BaseDirectory;
            if (tag is not null || !MacLayout.HasDashboardIn(source))
            {
                log("Downloading the dashboard");
                downloaded = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
                var zip = await Updates.DownloadReleaseAsset(MacLayout.ZipAsset(engineOnly: false), downloaded, isPattern: true, tag);
                await runner.Check("/usr/bin/ditto", "-x", "-k", zip, downloaded);
                source = Path.Combine(downloaded, "Dockyard.app", "Contents", "MacOS");
            }
            await runner.Run("/usr/bin/pkill", "-f", Path.Combine(MacLayout.AppBase, "Dockyard[. ]Dashboard"));
            foreach (var old in MacLayout.DashboardFiles(MacLayout.AppBase)) File.Delete(old);
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
        await runner.Run("/usr/bin/pkill", "-f", Path.Combine(MacLayout.AppBase, "Dockyard[. ]Dashboard"));
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

        await InstallCli(log, MacLayout.Cli);
        foreach (var (component, plugin, release) in new[] { ("Compose", "docker-compose", MacLayout.Compose), ("Buildx", "docker-buildx", MacLayout.Buildx) })
        {
            if (request.Without.Contains(component)) MacLayout.RemovePlugin(Path.Combine(MacLayout.PluginDir, plugin), MacLayout.PluginLinksDir);
            else await InstallPlugin(log, plugin, release);
        }
        if (request.Without.Contains(Kind.Id)) RemoveKind(log);
        else if (await Installed(Kind.Id) != Kind.Version) await InstallKind(log);

        await AdministratorSteps(log, request.Silent);
        await InstallCredentialHelper(log);
        LinkUserSocket(log);

        await PrepareForInstall(log);
        await WriteEngineConfig(log);

        var host = "unix://" + MacLayout.DockerSocket;
        var exists = (await runner.Run(MacLayout.DockerCli, "context", "inspect", MacLayout.Context)).ExitCode == 0;
        await runner.Check(MacLayout.DockerCli, "context", exists ? "update" : "create", MacLayout.Context, "--docker", $"host={host}");
        var current = (await runner.Run(MacLayout.DockerCli, "context", "show")).Output.Trim();
        if (current.Length > 0 && current != MacLayout.Context) await File.WriteAllTextAsync(MacLayout.PreviousContextFile, current);
        await runner.Check(MacLayout.DockerCli, "context", "use", MacLayout.Context);
        log($"Docker context '{MacLayout.Context}' → {host}");

        if (request.TrustHttps) await Https.Enable(this, log, request.Silent);

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

    static MacLayout.SocketUse SystemSocketUse()
    {
        var info = new FileInfo(MacLayout.SystemSocket);
        return MacLayout.SystemSocketUse(info.LinkTarget, info.Exists, MacLayout.DockerSocket);
    }

    // Rosetta, the PATH entry and /var/run/docker.sock need an administrator. A silent setup shows no dialog: then it needs
    // passwordless sudo, and without that the rest of Dockyard still works and the log says what is left.
    async Task AdministratorSteps(Action<string> log, bool silent)
    {
        List<string> steps = [];
        List<string> what = [];
        var rosetta = (await runner.Run("/usr/bin/arch", "-x86_64", "/usr/bin/true")).ExitCode == 0;
        if (!rosetta)
        {
            steps.Add("/usr/sbin/softwareupdate --install-rosetta --agree-to-license");
            what.Add("installing Rosetta for amd64 images");
        }
        if (!File.Exists("/etc/paths.d/dockyard") || await PathEntryIs(MacLayout.BinDir))
        {
            steps.Add(MacLayout.PathsEntry((await runner.Check("/usr/bin/id", "-u")).Trim(), MacLayout.BinDir));
            what.Add($"adding docker and kind to the PATH through {MacLayout.SharedBinDir}");
        }

        var socketUse = SystemSocketUse();
        var plist = MacLayout.SocketDaemonPlist(MacLayout.DockerSocket);
        if (socketUse == MacLayout.SocketUse.SomeoneElse)
            log($"{MacLayout.SystemSocket} belongs to another Docker app and stays as it is: tools that ignore docker contexts keep using that app");
        else if (!File.Exists(MacLayout.SocketDaemon) || await File.ReadAllTextAsync(MacLayout.SocketDaemon) != plist)
        {
            steps.Add(MacLayout.InstallSocketDaemon(plist));
            what.Add($"making {MacLayout.SystemSocket} point at Dockyard");
        }
        if (!File.Exists(MacLayout.DomainResolver))
        {
            steps.Add(MacLayout.InstallDomainResolver);
            what.Add("letting this Mac look up container domains");
        }
        if (steps.Count == 0) return;

        var script = string.Join(" ; ", steps);
        log("Administrator step: " + string.Join(", ", what));
        var r = silent
            ? await runner.Run("sudo", "-n", "/bin/sh", "-c", script)
            : await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script));
        if (r.ExitCode != 0)
            log($"Skipped ({(silent ? "no passwordless sudo" : "approval declined")}): {string.Join(", ", what)}. Running the wizard with an administrator's approval adds them; everything else works without them.");
    }

    static async Task InstallCredentialHelper(Action<string> log)
    {
        try
        {
            if (!File.Exists(MacLayout.CredentialHelper))
            {
                Directory.CreateDirectory(MacLayout.BinDir);
                await Pinned.Download(MacLayout.CredentialHelperRelease, MacLayout.CredentialHelper);
            }
            // docker finds the helper through the PATH: a store it cannot find would break every docker login, also from other Docker apps.
            if (File.Exists("/etc/paths.d/dockyard"))
                await CredentialStore.Adopt(log, "osxkeychain", MacLayout.CredentialHelper, MacLayout.CredentialStoreMarker);
            else
                log("docker login does not use the Keychain until ~/.dockyard/bin is on the PATH; running the wizard with an administrator's approval adds it");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            log($"The credential store was not set up ({e.Message}); running the wizard again retries it");
        }
    }

    // Tools look here when /var/run/docker.sock is not theirs to use; Docker Desktop's own socket lives here too and stays.
    static void LinkUserSocket(Action<string> log)
    {
        var link = new FileInfo(MacLayout.UserSocketLink);
        if (!link.Exists && link.LinkTarget is null)
        {
            Directory.CreateDirectory(link.DirectoryName!);
            File.CreateSymbolicLink(MacLayout.UserSocketLink, MacLayout.DockerSocket);
        }
        // Testcontainers would otherwise stop using the other app that owns /var/run/docker.sock.
        if (SystemSocketUse() != MacLayout.SocketUse.SomeoneElse && !File.Exists(MacLayout.TestcontainersFile))
        {
            File.WriteAllText(MacLayout.TestcontainersFile, MacLayout.TestcontainersLine + "\n");
            log("Testcontainers uses Dockyard");
        }
    }

    static void UnlinkUserSocket()
    {
        if (new FileInfo(MacLayout.UserSocketLink).LinkTarget == MacLayout.DockerSocket) File.Delete(MacLayout.UserSocketLink);
        if (File.Exists(MacLayout.TestcontainersFile) && File.ReadAllText(MacLayout.TestcontainersFile).Trim() == MacLayout.TestcontainersLine)
            File.Delete(MacLayout.TestcontainersFile);
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
        var bundles = MacLayout.InstalledBundles(File.Exists(MacLayout.LaunchAgent) ? await File.ReadAllTextAsync(MacLayout.LaunchAgent) : null, MacLayout.UserApplications);
        var own = MacLayout.Bundle(AppContext.BaseDirectory);
        foreach (var bundle in bundles.Where(b => b != own))
            await runner.Run("/usr/bin/pkill", "-f", Path.Combine(bundle, "Contents", "MacOS", "Dockyard"));
        if (File.Exists(MacLayout.LaunchAgent)) File.Delete(MacLayout.LaunchAgent);
        if (File.Exists(MacLayout.DockerCli))
        {
            var previous = File.Exists(MacLayout.PreviousContextFile) ? await File.ReadAllTextAsync(MacLayout.PreviousContextFile) : null;
            await runner.Run(MacLayout.DockerCli, "context", "use", MacLayout.ContextToRestore(previous));
            await runner.Run(MacLayout.DockerCli, "context", "rm", "-f", MacLayout.Context);
        }
        await Https.Disable(this, log);
        await CredentialStore.Forget(MacLayout.CredentialStoreMarker, MacLayout.CredentialHelper, log);
        await EngineConfig.ApplyClientProxy("unix://" + MacLayout.DockerSocket, null);
        UnlinkUserSocket();
        List<string> steps = [];
        if (await PathEntryIs(MacLayout.BinDir) || await PathEntryIs(MacLayout.SharedBinDir)) steps.Add(MacLayout.RemovePathsEntry);
        if (File.Exists(MacLayout.DomainResolver)) steps.Add($"rm -f {MacLayout.DomainResolver}");
        if (File.Exists(MacLayout.SocketDaemon) || SystemSocketUse() == MacLayout.SocketUse.Ours) steps.Add(MacLayout.RemoveSocketDaemon(MacLayout.DockerSocket));
        if (steps.Count > 0)
        {
            var script = string.Join(" ; ", steps);
            if ((await runner.Run("sudo", "-n", "/bin/sh", "-c", script)).ExitCode != 0)
                await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script));
        }
        if (Directory.Exists(MacLayout.BinDir)) Directory.Delete(MacLayout.BinDir, recursive: true);
        foreach (var plugin in new[] { "docker-compose", "docker-buildx" })
            MacLayout.RemovePlugin(Path.Combine(MacLayout.PluginDir, plugin), MacLayout.PluginLinksDir);
        foreach (var bundle in bundles)
        {
            try { Directory.Delete(bundle, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{bundle} could not be removed ({e.Message}); move it to the Trash"); }
        }
        if (removeData)
        {
            if (Directory.Exists(MacLayout.DataDir)) Directory.Delete(MacLayout.DataDir, recursive: true);
            log("Removed your images and containers");
        }
        else log($"Your images and containers were kept in {Path.Combine(MacLayout.DataDir, "data.img")}");
    }

    static async Task<bool> PathEntryIs(string dir) =>
        File.Exists("/etc/paths.d/dockyard") && (await File.ReadAllTextAsync("/etc/paths.d/dockyard")).Trim() == dir;

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

    async Task<ProxySettings?> HostProxy()
    {
        if (HostProxySettings.FromEnvironment(Environment.GetEnvironmentVariable) is { } fromEnvironment) return fromEnvironment;
        var r = await runner.Run("/usr/sbin/scutil", "--proxy");
        return r.ExitCode == 0 ? HostProxySettings.FromScutil(r.Output) : null;
    }

    // Before every boot, because what the host uses changes with the network it is on.
    async Task WriteEngineConfig(Action<string>? log = null)
    {
        var proxy = await HostProxy();
        Directory.CreateDirectory(Path.GetDirectoryName(MacLayout.EngineConfigFile)!);
        await File.WriteAllTextAsync(MacLayout.EngineConfigFile, EngineConfig.Render(proxy, Settings.Load()));
        await EngineConfig.ApplyClientProxy("unix://" + MacLayout.DockerSocket, proxy);
        if (proxy is { IsEmpty: false }) log?.Invoke("The engine and containers use this Mac's proxy");
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
            await Pinned.Download(release, tgz);
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

    static async Task InstallKind(Action<string> log)
    {
        Directory.CreateDirectory(MacLayout.BinDir);
        await Kind.Download(MacLayout.KindCli);
        log($"kind {Kind.Tag} in {MacLayout.BinDir}");
    }

    static void RemoveKind(Action<string> log)
    {
        if (!File.Exists(MacLayout.KindCli)) return;
        File.Delete(MacLayout.KindCli);
        log("Removed kind");
    }

    async Task InstallPlugin(Action<string> log, string name, Release release)
    {
        Directory.CreateDirectory(MacLayout.PluginDir);
        var target = Path.Combine(MacLayout.PluginDir, name);
        MacLayout.SetAsideLinkedPlugin(target, MacLayout.PluginLinksDir);
        await Pinned.Download(release, target);
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

    static bool HelperAnswers()
    {
        if (!File.Exists(MacLayout.ControlSocket)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(MacLayout.ControlSocket));
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
