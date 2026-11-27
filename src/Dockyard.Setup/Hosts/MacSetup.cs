using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Installing and uninstalling on a Mac: the app bundle, the administrator steps, the docker context, the credential store,
// the sockets other tools look for and the login item.
[SupportedOSPlatform("macos")]
sealed class MacSetup(ICommandRunner runner, MacComponents components, MacVm vm, ToolComponents tools)
{
    public static bool RunsFromBundle => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) == Path.TrimEndingDirectorySeparator(MacLayout.AppBase);
    public static bool RunsFromInstallLocation => RunsFromBundle || Paths.Self == MacLayout.CliExe;

    public async Task Install(Action<string> log, InstallRequest request, IEngineHost host)
    {
        log(InstallPhases.Start(InstallPhases.Dockyard));
        var cli = !request.Without.Contains(nameof(Component.DockyardCli));
        await PlaceApps(log, request.Dashboard, request.Tray, cli);

        log(InstallPhases.Start(InstallPhases.MacTools));
        if (tools.Packages is null) log("The tools come from Dockyard, in " + MacLayout.BinDir);
        else if (!await tools.Packages.IsAvailable()) log($"No Homebrew here, so the tools come from Dockyard, in {MacLayout.BinDir}");
        foreach (var tool in Tools.All)
        {
            if (request.Without.Contains(tool.Id)) await tools.RemoveDockyardCopy(log, tool);
            else await tools.Ensure(log, tool);
        }
        await FindBrewPlugins(log, tools);

        log(InstallPhases.Start(InstallPhases.Administrator));
        await AdministratorSteps(log, request.Silent, cli);
        await InstallCredentialHelper(log);
        LinkUserSocket(log);

        log(InstallPhases.Start(InstallPhases.LinuxVm));
        await vm.PrepareForInstall(log);
        await vm.WriteEngineConfig(log);

        var dockerHost = "unix://" + MacLayout.DockerSocket;
        var exists = (await runner.Run(MacLayout.Docker, "context", "inspect", MacLayout.Context)).ExitCode == 0;
        await runner.Check(MacLayout.Docker, "context", exists ? "update" : "create", MacLayout.Context, "--docker", $"host={dockerHost}");
        var current = (await runner.Run(MacLayout.Docker, "context", "show")).Output.Trim();
        if (current.Length > 0 && current != MacLayout.Context) await File.WriteAllTextAsync(MacLayout.PreviousContextFile, current);
        await runner.Check(MacLayout.Docker, "context", "use", MacLayout.Context);
        log($"Docker context '{MacLayout.Context}' → {dockerHost}");

        if (request.Tray) await AddLoginItem(log);
        else RemoveLoginItem(log);

        if (request.TrustHttps)
        {
            log(InstallPhases.Start(InstallPhases.TrustedHttps));
            await Https.EnableInSetup(host, Settings.Load(), log, request.Silent);
        }
    }

    public static async Task AddLoginItem(Action<string> log)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MacLayout.LaunchAgent)!);
        await File.WriteAllTextAsync(MacLayout.LaunchAgent, MacLayout.LaunchAgentPlist(MacLayout.InstalledExe));
        log($"{Paths.AppName} starts at login ({MacLayout.LaunchAgent})");
    }

    public static void RemoveLoginItem(Action<string> log)
    {
        if (!File.Exists(MacLayout.LaunchAgent)) return;
        File.Delete(MacLayout.LaunchAgent);
        log($"{Paths.AppName} no longer starts at login");
    }

    // The bundles are found through the login item, which the first stage removes.
    public Task Uninstall(Action<string> log, bool removeData, bool silent, IEngineHost host)
    {
        var bundles = InstalledBundles();
        return Uninstall(log, removeData, RemoveUserParts(silent, host, bundles), RemoveSystemParts, (l, data) => RemoveApp(l, data, bundles));
    }

    // The administrator step comes before the app is deleted: if it is declined or fails, the app, which is the command,
    // is still there to run the uninstall again.
    internal static async Task Uninstall(Action<string> log, bool removeData, Func<Action<string>, Task> user, Func<Action<string>, Task> system, Func<Action<string>, bool, Task> app)
    {
        await user(log);
        await system(log);
        await app(log, removeData);
    }

    Func<Action<string>, Task> RemoveUserParts(bool silent, IEngineHost host, IReadOnlyList<string> bundles) => async log =>
    {
        await vm.Stop();
        await runner.Run("/bin/sh", "-c", MacLayout.RemoveHost);
        var own = MacLayout.Bundle(AppContext.BaseDirectory);
        foreach (var bundle in bundles.Where(b => b != own))
            await runner.Run("/usr/bin/pkill", "-f", Path.Combine(bundle, "Contents", "MacOS", "Dockyard"));
        if (File.Exists(MacLayout.LaunchAgent)) File.Delete(MacLayout.LaunchAgent);
        if (File.Exists(MacLayout.Docker))
        {
            var previous = File.Exists(MacLayout.PreviousContextFile) ? await File.ReadAllTextAsync(MacLayout.PreviousContextFile) : null;
            await runner.Run(MacLayout.Docker, "context", "use", MacLayout.ContextToRestore(previous));
            await runner.Run(MacLayout.Docker, "context", "rm", "-f", MacLayout.Context);
        }
        await Https.Disable(host, Settings.Load(), log, silent);
        await CredentialStore.Forget(MacLayout.CredentialStoreMarker, MacLayout.CredentialHelper, log);
        await EngineConfig.ApplyClientProxy("unix://" + MacLayout.DockerSocket, null);
        UnlinkUserSocket();
    };

    static IReadOnlyList<string> InstalledBundles() =>
        MacLayout.InstalledBundles(File.Exists(MacLayout.LaunchAgent) ? File.ReadAllText(MacLayout.LaunchAgent) : null, MacLayout.UserApplications);

    async Task RemoveSystemParts(Action<string> log)
    {
        var steps = MacLayout.AdministratorUninstall(
            pathEntry: await PathEntryIs(MacLayout.SharedBinDir),
            resolver: File.Exists(MacLayout.DomainResolver),
            socketDaemon: File.Exists(MacLayout.SocketDaemon) || SystemSocketUse() == MacLayout.SocketUse.Ours,
            cliLink: new FileInfo(MacLayout.CliLink).LinkTarget is not null,
            MacLayout.DockerSocket);
        if (steps.Count == 0) return;
        var script = string.Join(" ; ", steps);
        if ((await runner.Run("sudo", "-n", "/bin/sh", "-c", script)).ExitCode != 0
            && (await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script))).ExitCode != 0)
            throw new InvalidOperationException("The administrator step was declined or failed, so Dockyard is still installed. Run the uninstall again with an administrator's approval.");
    }

    Task RemoveApp(Action<string> log, bool removeData, IReadOnlyList<string> bundles)
    {
        foreach (var plugin in new[] { "docker-compose", "docker-buildx" })
            MacComponents.RemovePlugin(plugin);
        foreach (var path in MacLayout.AppFilesToRemove(bundles, Paths.Self))
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{path} could not be removed ({e.Message}); move it to the Trash"); }
        }
        if (removeData)
        {
            if (Directory.Exists(MacLayout.DataDir)) Directory.Delete(MacLayout.DataDir, recursive: true);
            log("Removed your images and containers");
        }
        else log($"Your images and containers were kept in {Path.Combine(MacLayout.DataDir, "data.img")}");
        return Task.CompletedTask;
    }

    // The link needs an administrator: without one, the link is left pointing nowhere and the next setup or the uninstall
    // takes it back.
    public async Task LinkCli(Action<string> log, bool linked)
    {
        var script = linked ? MacLayout.InstallCliLink(MacLayout.CliExe) : MacLayout.RemoveCliLink;
        if (!linked && new FileInfo(MacLayout.CliLink).LinkTarget != MacLayout.CliExe) return;
        if ((await runner.Run("sudo", "-n", "/bin/sh", "-c", script)).ExitCode == 0
            || linked && (await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script))).ExitCode == 0) return;
        log(linked
            ? $"The dockyard command is in {MacLayout.CliExe}; running dockyard setup with an administrator's approval adds it to {MacLayout.CliLink}"
            : $"{MacLayout.CliLink} still points at the removed command; the next setup or the uninstall takes it back");
    }

    // The bundle when the dashboard or the tray is wanted and none is there yet, or when an app runs from one somewhere else
    // (a disk image or a translocated copy); an installed bundle is otherwise the dashboard's to update. The CLI goes in
    // ~/.dockyard/cli, and without a bundle the engine's parts go in ~/.dockyard too.
    async Task PlaceApps(Action<string> log, bool dashboard, bool tray, bool cli)
    {
        var running = MacLayout.Bundle(AppContext.BaseDirectory);
        if (running is not null && !RunsFromBundle) await components.PlaceBundle(log, running);
        else if (!MacComponents.HasBundle && MacLayout.WantsBundle(dashboard, tray)) await components.PlaceBundle(log, fromBundle: null);
        if (MacComponents.HasBundle)
        {
            if (MacComponents.HasDashboard && !dashboard) await components.RemoveDashboard(log);
            else if (!MacComponents.HasDashboard && dashboard) await components.AddDashboard(log);
        }

        if (cli) await components.PlaceCli(log);
        else MacComponents.RemoveCli(log);

        if (MacLayout.HasBundleHelper) MacComponents.RemoveEngineParts(log);
        else await components.PlaceEngineParts(log);
    }

    static MacLayout.SocketUse SystemSocketUse()
    {
        var info = new FileInfo(MacLayout.SystemSocket);
        return MacLayout.SystemSocketUse(info.LinkTarget, info.Exists, MacLayout.DockerSocket);
    }

    // Rosetta, the PATH entry and /var/run/docker.sock need an administrator. A silent setup shows no dialog: then it needs
    // passwordless sudo, and without that the rest of Dockyard still works and the log says what is left.
    async Task AdministratorSteps(Action<string> log, bool silent, bool cli)
    {
        List<string> steps = [];
        List<string> what = [];
        var rosetta = (await runner.Run("/usr/bin/arch", "-x86_64", "/usr/bin/true")).ExitCode == 0;
        if (!rosetta)
        {
            steps.Add("/usr/sbin/softwareupdate --install-rosetta --agree-to-license");
            what.Add("installing Rosetta for amd64 images");
        }
        var uid = (await runner.Check("/usr/bin/id", "-u")).Trim();
        var brewBin = Homebrew.FindPrefix() is { } prefix ? Path.Combine(prefix, "bin") : null;
        if (!File.Exists("/etc/paths.d/dockyard") || !MacLayout.WrappersAreCurrent(uid, MacLayout.BinDir, brewBin))
        {
            steps.Add(MacLayout.PathsEntry(uid, MacLayout.BinDir, brewBin));
            what.Add($"adding docker, kind, kubectl and helm to the PATH through {MacLayout.SharedBinDir}");
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
        var cliLink = new FileInfo(MacLayout.CliLink).LinkTarget;
        if (cli && cliLink != MacLayout.CliExe)
        {
            steps.Add(MacLayout.InstallCliLink(MacLayout.CliExe));
            what.Add($"adding the dockyard command to {MacLayout.CliLink}");
        }
        else if (!cli && cliLink == MacLayout.CliExe)
        {
            steps.Add(MacLayout.RemoveCliLink);
            what.Add($"taking the dockyard command out of {MacLayout.CliLink}");
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
            log($"Skipped ({(silent ? "no passwordless sudo" : "approval declined")}): {string.Join(", ", what)}. Running dockyard setup with an administrator's approval adds them; everything else works without them.");
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
                log("docker login does not use the Keychain until ~/.dockyard/bin is on the PATH; running dockyard setup with an administrator's approval adds it");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            log($"The credential store was not set up ({e.Message}); running dockyard setup again retries it");
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

    public static async Task FindBrewPlugins(Action<string> log, ToolComponents tools)
    {
        if (tools.Packages is Homebrew { CliPlugins: { } dir } && (await tools.IsManaged(Tools.Compose) || await tools.IsManaged(Tools.Buildx)))
            await DockerPlugins.AddExtraDir(log, dir);
    }

    static async Task<bool> PathEntryIs(string dir) =>
        File.Exists("/etc/paths.d/dockyard") && (await File.ReadAllTextAsync("/etc/paths.d/dockyard")).Trim() == dir;
}
