using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Installing and uninstalling on a Mac: the app bundle, the administrator steps, the docker context, the credential store,
// the sockets other tools look for and the login item.
[SupportedOSPlatform("macos")]
sealed class MacSetup(ICommandRunner runner, MacComponents components, MacVm vm)
{
    public static bool RunsFromInstallLocation => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == Path.TrimEndingDirectorySeparator(MacLayout.AppBase);

    public async Task Install(Action<string> log, InstallRequest request, IEngineHost host)
    {
        await InstallBundle(log, request.Dashboard);

        await components.InstallCli(log, MacLayout.Cli);
        foreach (var (component, plugin, release) in new[] { ("Compose", "docker-compose", MacLayout.Compose), ("Buildx", "docker-buildx", MacLayout.Buildx) })
        {
            if (request.Without.Contains(component)) MacComponents.RemovePlugin(plugin);
            else await MacComponents.InstallPlugin(log, plugin, release);
        }
        if (request.Without.Contains(Kind.Id)) MacComponents.RemoveKind(log);
        else if (await components.Installed(Kind.Id) != Kind.Version) await MacComponents.InstallKind(log);

        await AdministratorSteps(log, request.Silent);
        await InstallCredentialHelper(log);
        LinkUserSocket(log);

        await vm.PrepareForInstall(log);
        await vm.WriteEngineConfig(log);

        var dockerHost = "unix://" + MacLayout.DockerSocket;
        var exists = (await runner.Run(MacLayout.DockerCli, "context", "inspect", MacLayout.Context)).ExitCode == 0;
        await runner.Check(MacLayout.DockerCli, "context", exists ? "update" : "create", MacLayout.Context, "--docker", $"host={dockerHost}");
        var current = (await runner.Run(MacLayout.DockerCli, "context", "show")).Output.Trim();
        if (current.Length > 0 && current != MacLayout.Context) await File.WriteAllTextAsync(MacLayout.PreviousContextFile, current);
        await runner.Check(MacLayout.DockerCli, "context", "use", MacLayout.Context);
        log($"Docker context '{MacLayout.Context}' → {dockerHost}");

        if (request.TrustHttps) await Https.Enable(host, log, request.Silent);

        Directory.CreateDirectory(Path.GetDirectoryName(MacLayout.LaunchAgent)!);
        await File.WriteAllTextAsync(MacLayout.LaunchAgent, MacLayout.LaunchAgentPlist(MacLayout.InstalledExe));
        log($"{Paths.AppName} starts at login ({MacLayout.LaunchAgent})");
    }

    public async Task Uninstall(Action<string> log, bool removeData, IEngineHost host)
    {
        await vm.Stop();
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
        await Https.Disable(host, log);
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
            MacComponents.RemovePlugin(plugin);
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

    // Copies the wizard's bundle to where the app belongs, from a download, a disk image or a translocated copy.
    // The login item and vm.json point at it, so it has to stay there. Nothing under ~/.dockyard is touched.
    async Task InstallBundle(Action<string> log, bool dashboard)
    {
        if (RunsFromInstallLocation)
        {
            if (MacComponents.HasDashboard && !dashboard) await components.RemoveDashboard(log);
            else if (!MacComponents.HasDashboard && dashboard) await components.AddDashboard(log);
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
        if (!dashboard) await components.RemoveDashboard(log);
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

    static async Task<bool> PathEntryIs(string dir) =>
        File.Exists("/etc/paths.d/dockyard") && (await File.ReadAllTextAsync("/etc/paths.d/dockyard")).Trim() == dir;
}
