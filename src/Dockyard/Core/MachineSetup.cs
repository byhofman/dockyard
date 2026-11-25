namespace Dockyard.Core;

// Runs elevated. Everything per-user lives in UserSetup. The Windows engine, the apps and tools, and the trust store each
// have a class of their own; this is the order they are set up and taken down in.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class MachineSetup
{
    // Returns true when a reboot is needed.
    public static async Task<bool> Install(Action<string> log, string userSid, string userName, int keepPid, bool engineOnly, IReadOnlyCollection<string> without,
        bool usePackageManager)
    {
        InUseFiles.CleanUp(Paths.DockerDir);
        var withWindows = !without.Contains(nameof(Component.Engine));
        var reboot = withWindows && await MachineEngine.EnableContainersFeature(log);
        if (!without.Contains(nameof(Component.LinuxEngine))) reboot |= await InstallWsl(log);
        await MachineApps.AddDefenderExclusions(log);
        if (withWindows) await MachineEngine.Update(log, onlyIfNewer: true);
        else await MachineEngine.Remove(log);
        MachineApps.AddToMachinePath(Paths.DockerDir);
        await InstallTools(log, without, usePackageManager);
        await MachineApps.InstallCredentialHelper(log);
        if (without.Contains(nameof(Component.LinuxEngine))) await UnrouteDomains();
        else await RouteDomains(log);
        if (withWindows) await MachineEngine.Configure(log, userSid, userName);
        await MachineApps.InstallApp(log, Paths.Self, keepPid, engineOnly, Updates.LocalAssets);
        MachineApps.RemoveLegacy(log, keepPid);
        return reboot;
    }

    // A tool left out loses only Dockyard's own copy: winget's may be the user's. One that will not install is no reason to
    // fail the rest, but docker is.
    static async Task InstallTools(Action<string> log, IReadOnlyCollection<string> without, bool usePackageManager)
    {
        var tools = MachineTools.For(usePackageManager);
        if (tools.Packages is { } winget && !await winget.IsAvailable()) log("winget is not available, so the tools are Dockyard's own copies, in Program Files");
        foreach (var tool in Tools.All)
        {
            if (without.Contains(tool.Id))
            {
                await tools.RemoveOwn(log, tool);
                continue;
            }
            try { await tools.Ensure(log, tool); }
            catch (Exception e) when (tool != Tools.Docker && e is InvalidOperationException or HttpRequestException or IOException)
            {
                log($"{tool.Title} was not installed ({e.Message}); running the wizard again retries it");
            }
        }
    }

    public static async Task Update(Action<string> log, Component component, string? source, int keepPid, bool usePackageManager)
    {
        if (Tools.Find(component.ToString()) is { } tool)
        {
            await MachineTools.For(usePackageManager).Install(log, tool);
            return;
        }
        switch (component)
        {
            case Component.Engine: await MachineEngine.Update(log, onlyIfNewer: false); break;
            case Component.App:
                // Defender's rule goes by path, and an update can bring the first dashboard with the new name.
                await MachineApps.AddDefenderExclusions(log);
                // Next to the new exe in the folder the tray downloaded and checked both into.
                var exe = source ?? throw new ArgumentException("--source is required for the app update");
                await MachineApps.InstallApp(log, exe, keepPid, engineOnly: !Paths.HasDashboard, Path.GetDirectoryName(Path.GetFullPath(exe)));
                Shell.StartUnelevated(Paths.AppExe);
                break;
            default: throw new ArgumentException($"{component} is not a machine component");
        }
    }

    public static async Task Uninstall(Action<string> log, int keepPid)
    {
        MachineApps.StopApps(keepPid);
        await UnrouteDomains();
        MachineTrust.UntrustCa(log);
        await MachineEngine.RemoveService(log);
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Remove-MpPreference -ControlledFolderAccessAllowedApplications {Shell.PsQuote(Paths.DockerdExe)}");
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Remove-MpPreference -AttackSurfaceReductionOnlyExclusions {string.Join(',', MachineApps.EverExcludedFromAsr.Select(Shell.PsQuote))}");
        await Shell.Run("net.exe", "localgroup", "docker-users", "/delete");
        MachineApps.RemoveFromMachinePath(Paths.DockerDir);
        MachineApps.RemoveFromMachinePath(MachineApps.CommandDir);
        MachineApps.RemoveLegacy(log, keepPid);

        foreach (var dir in new[] { Paths.DockerDir, Paths.AppDir })
        {
            if (!Directory.Exists(dir)) continue;
            log($"Removing {dir}");
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, Paths.Self, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); } catch (IOException) { log($"  in use, left behind: {file}"); }
            }
        }
        log($"Windows images and containers under {Path.GetDirectoryName(Paths.DaemonConfigDir)} were left in place");
    }

    public static async Task SetComponent(Action<string> log, Component component, bool installed, string userSid, string userName, bool usePackageManager)
    {
        if (Tools.Find(component.ToString()) is { } tool)
        {
            var tools = MachineTools.For(usePackageManager);
            if (installed) await tools.Install(log, tool);
            else await tools.Remove(log, tool);
            return;
        }
        switch (component)
        {
            // The distro and its images are the user's half, in UserSetup. Only the engine answers those names.
            case Component.LinuxEngine:
                if (installed) await RouteDomains(log);
                else await UnrouteDomains();
                break;
            case Component.Engine:
                if (!installed) { await MachineEngine.Remove(log); break; }
                if (await MachineEngine.EnableContainersFeature(log)) log("Windows has to restart before the Windows engine can run");
                await MachineEngine.Update(log, onlyIfNewer: true);
                await MachineEngine.Configure(log, userSid, userName);
                break;
            default: throw new ArgumentException($"{component} cannot be changed here");
        }
    }

    public static async Task UpdateDashboard(Action<string> log, string sourceDir, int keepPid)
    {
        MachineApps.StopApps(keepPid, dashboardOnly: true);
        await MachineApps.AddDefenderExclusions(log);
        await MachineApps.InstallDashboard(log, sourceDir);
    }

    public static async Task SetDashboard(Action<string> log, bool installed, int keepPid)
    {
        MachineApps.StopApps(keepPid, dashboardOnly: true);
        if (installed) await MachineApps.InstallDashboard(log, Updates.LocalAssets);
        else MachineApps.RemoveDashboard(log);
    }

    const string DomainNamespace = ".dockyard.test";

    // A rule for one name space only: everything else keeps using the DNS servers Windows was using.
    static async Task RouteDomains(Action<string> log)
    {
        var r = await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"if (-not (Get-DnsClientNrptRule | Where-Object {{ $_.Namespace -contains '{DomainNamespace}' }})) {{ Add-DnsClientNrptRule -Namespace '{DomainNamespace}' -NameServers '{DomainRelay.Address}' }}");
        log(r.ExitCode == 0 ? $"Windows asks Dockyard about names under {DomainNamespace}" : $"Container domains were not set up ({r.Error.Trim()})");
    }

    static async Task UnrouteDomains() => await Shell.Run("powershell.exe", "-NoProfile", "-Command",
        $"Get-DnsClientNrptRule | Where-Object {{ $_.Namespace -contains '{DomainNamespace}' }} | Remove-DnsClientNrptRule -Force");

    // Returns true when a reboot has to finish the WSL install.
    static async Task<bool> InstallWsl(Action<string> log)
    {
        if ((await Shell.Run("wsl.exe", "--status")).ExitCode == 0) { log("WSL is ready"); return false; }

        log("Installing WSL");
        var install = await Shell.Run("wsl.exe", "--install", "--no-distribution");
        if (install.ExitCode is not (0 or Elevation.RebootExitCode))
            throw new InvalidOperationException($"wsl --install failed ({install.ExitCode}): {(install.Error + install.Output).Replace("\0", "").Trim()}");

        if (install.ExitCode == 0 && (await Shell.Run("wsl.exe", "--status")).ExitCode == 0) { log("WSL is ready"); return false; }
        log("WSL is installed; a restart finishes it");
        return true;
    }
}
