using System.Diagnostics;
using System.IO.Compression;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;

namespace Dockyard.Core;

// Runs elevated. Everything per-user lives in UserSetup.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class MachineSetup
{
    static readonly (Component Component, string File, string Repo, string Pattern)[] Plugins =
    [
        (Component.Compose, "docker-compose.exe", "docker/compose", @"^docker-compose-windows-x86_64\.exe$"),
        (Component.Buildx, "docker-buildx.exe", "docker/buildx", @"^buildx-v[\d.]+\.windows-amd64\.exe$"),
    ];

    // Returns true when a reboot is needed.
    public static async Task<bool> Install(Action<string> log, string userSid, string userName, int keepPid, bool engineOnly = false)
    {
        InUseFiles.CleanUp(Paths.DockerDir);
        var reboot = await EnableContainersFeature(log);
        reboot |= await InstallWsl(log);
        await AddDefenderExclusions(log);
        await UpdateEngine(log, onlyIfNewer: true);
        AddToMachinePath(Paths.DockerDir);
        foreach (var plugin in Plugins) await UpdatePlugin(log, plugin.Component);
        await InstallCredentialHelper(log);
        await RouteDomains(log);
        await ConfigureWindowsEngine(log, userSid, userName);
        await InstallApp(log, Paths.Self, keepPid, engineOnly);
        RemoveLegacy(log, keepPid);
        return reboot;
    }

    public static async Task Update(Action<string> log, Component component, string? source, int keepPid)
    {
        switch (component)
        {
            case Component.Engine: await UpdateEngine(log, onlyIfNewer: false); break;
            case Component.Compose or Component.Buildx: await UpdatePlugin(log, component); break;
            case Component.App:
                await InstallApp(log, source ?? throw new ArgumentException("--source is required for the app update"), keepPid, engineOnly: !File.Exists(Paths.DashboardExe));
                Shell.StartUnelevated(Paths.AppExe);
                break;
            default: throw new ArgumentException($"{component} is not a machine component");
        }
    }

    public static async Task Uninstall(Action<string> log, int keepPid)
    {
        StopApps(keepPid);
        await UnrouteDomains();
        UntrustCa(log);
        if (ServiceExists())
        {
            log("Removing docker service");
            await StopService();
            await Shell.Check("sc.exe", "delete", Paths.ServiceName);
            RemoveEventSource();
        }
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Remove-MpPreference -ControlledFolderAccessAllowedApplications '{Paths.DockerdExe}'");
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Remove-MpPreference -AttackSurfaceReductionOnlyExclusions {string.Join(',', ExcludedFromAsr.Select(p => $"'{p}'"))}");
        await Shell.Run("net.exe", "localgroup", "docker-users", "/delete");
        RemoveFromMachinePath(Paths.DockerDir);
        RemoveLegacy(log, keepPid);

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

    public static void TrustCa(Action<string> log, string certificateFile)
    {
        using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(File.ReadAllText(certificateFile));
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.Root, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
        store.Add(certificate);
        log("Windows trusts Dockyard's certificate, for names under dockyard.test only");
    }

    public static void UntrustCa(Action<string> log)
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(System.Security.Cryptography.X509Certificates.StoreName.Root, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
        var removed = 0;
        foreach (var certificate in store.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindBySubjectName, LocalCa.SubjectName, validOnly: false))
        {
            store.Remove(certificate);
            removed++;
        }
        if (removed > 0) log("Dockyard's certificate is out of Windows' trust store");
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

    // Not worth failing an install over: without it docker login just keeps working the way it did.
    static async Task InstallCredentialHelper(Action<string> log)
    {
        if (File.Exists(Paths.CredentialHelper)) return;
        try
        {
            var release = await Updates.LatestGitHub(Paths.CredentialHelpersRepo, @"^docker-credential-wincred-v[\d.]+\.windows-amd64\.exe$");
            await Updates.DownloadFile(release.DownloadUrl!, Paths.CredentialHelper);
            log($"Credential helper {release.Tag}");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            log($"The credential helper was not installed ({e.Message}); running the wizard again retries it");
        }
    }

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

    static IEnumerable<string> ExcludedFromAsr => new[] { Paths.AppExe, Paths.DashboardExe };

    // Defender's prevalence/age rule (event 1121) blocks every new build, and an exclusion goes by path because a hash changes with each build.
    static async Task AddDefenderExclusions(Action<string> log)
    {
        var paths = ExcludedFromAsr.Append(Paths.Self).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => $"'{p}'");
        var r = await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Add-MpPreference -AttackSurfaceReductionOnlyExclusions {string.Join(',', paths)}");
        log(r.ExitCode == 0 ? "Defender: Dockyard is excluded from the prevalence rule" : "Defender exclusions were not added (managed device?); ask your administrator if Defender blocks Dockyard");
    }

    static async Task<bool> EnableContainersFeature(Action<string> log)
    {
        var info = await Shell.Check("dism.exe", "/online", "/get-featureinfo", "/featurename:Containers", "/English");
        if (Regex.IsMatch(info, @"State : Enabled")) { log("Containers feature already enabled"); return false; }

        log("Enabling Windows Containers feature");
        var r = await Shell.Run("dism.exe", "/online", "/enable-feature", "/featurename:Containers", "/all", "/norestart", "/quiet");
        if (r.ExitCode == 3010) { log("Containers feature enabled (reboot required)"); return true; }
        if (r.ExitCode != 0) throw new InvalidOperationException($"Enabling Containers feature failed ({r.ExitCode}): {r.Output.Trim()}");
        return false;
    }

    static async Task UpdateEngine(Action<string> log, bool onlyIfNewer)
    {
        var latest = await Updates.LatestStatic();
        var current = await Updates.Installed(Component.Engine);
        if (onlyIfNewer && current >= latest.Version) { log($"Docker {current} is up to date"); return; }

        log($"Installing Docker {latest.Version}" + (current is null ? "" : $" (was {current})"));
        var wasRunning = ServiceExists() && ServiceStatus() == ServiceControllerStatus.Running;
        await StopService();

        var zip = Path.Combine(Path.GetTempPath(), $"docker-{latest.Version}.zip");
        await Updates.DownloadFile(latest.DownloadUrl!, zip);
        var root = Path.GetDirectoryName(Paths.DockerDir)!;
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.Combine(root, entry.FullName.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                InUseFiles.MoveAside(target);
                entry.ExtractToFile(target);
            }
        }
        File.Delete(zip);

        if (wasRunning)
        {
            using var sc = new ServiceController(Paths.ServiceName);
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)));
        }
    }

    static async Task UpdatePlugin(Action<string> log, Component component)
    {
        var plugin = Plugins.Single(p => p.Component == component);
        var release = await Updates.LatestGitHub(plugin.Repo, plugin.Pattern);
        Directory.CreateDirectory(Paths.PluginDir);
        var target = Path.Combine(Paths.PluginDir, plugin.File);
        var incoming = target + ".new";
        await Updates.DownloadFile(release.DownloadUrl!, incoming);
        InUseFiles.MoveAside(target);
        File.Move(incoming, target);
        log($"{plugin.File} {release.Tag}");
    }

    static async Task ConfigureWindowsEngine(Action<string> log, string userSid, string userName)
    {
        if ((await Shell.Run("net.exe", "localgroup", "docker-users")).ExitCode != 0)
            await Shell.Check("net.exe", "localgroup", "docker-users", "/add", "/comment:Access to the Windows Docker engine");
        var add = await Shell.Run("net.exe", "localgroup", "docker-users", userName, "/add");
        if (add.ExitCode == 0) log($"Added {userName} to docker-users");
        else if (!(add.Output + add.Error).Contains("1378"))
            throw new InvalidOperationException($"Adding {userName} to docker-users failed: {(add.Error + add.Output).Trim()}");

        // Controlled Folder Access otherwise blocks dockerd unpacking image layers ("hcsshim::ProcessBaseLayer ... Access is denied").
        // When Intune manages this list it may override the local entry; then it has to be added there.
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Add-MpPreference -ControlledFolderAccessAllowedApplications '{Paths.DockerdExe}'");

        Directory.CreateDirectory(Paths.DaemonConfigDir);
        var daemonJson = Path.Combine(Paths.DaemonConfigDir, "daemon.json");
        var existing = File.Exists(daemonJson) ? File.ReadAllText(daemonJson) : null;
        var config = DaemonJson.WithAccessFor(existing, Paths.WindowsHost, ResolvesTo(userName, userSid) ? userName : null);
        File.WriteAllText(daemonJson, config);
        var changed = existing != config;

        if (!ServiceExists())
        {
            log("Registering docker service");
            RemoveEventSource();
            await Shell.Check(Paths.DockerdExe, "--register-service");
        }
        await Shell.Check("sc.exe", "config", Paths.ServiceName, "start=", "demand");

        var ace = $"(A;;CCLCSWRPWPDTLOCRRC;;;{userSid})";
        var sddl = (await Shell.Check("sc.exe", "sdshow", Paths.ServiceName)).Trim();
        if (!sddl.Contains(ace))
        {
            sddl = sddl.Contains("S:") ? sddl.Replace("S:", ace + "S:") : sddl + ace;
            await Shell.Check("sc.exe", "sdset", Paths.ServiceName, sddl);
        }
        log($"Windows engine: on demand, start/stop allowed for {userName}");

        if (changed && ServiceStatus() == ServiceControllerStatus.Running)
        {
            await StopService();
            using var sc = new ServiceController(Paths.ServiceName);
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)));
        }
    }

    // dockerd refuses to start when it cannot resolve a name in "group", so only a name that resolves goes in.
    static bool ResolvesTo(string userName, string userSid)
    {
        try
        {
            return new NTAccount(userName).Translate(typeof(SecurityIdentifier)).Value == userSid;
        }
        catch (Exception e) when (e is IdentityNotMappedException or SystemException)
        {
            return false;
        }
    }

    static async Task InstallApp(Action<string> log, string source, int keepPid, bool engineOnly)
    {
        var isInstalledCopy = string.Equals(Path.GetFullPath(source), Paths.AppExe, StringComparison.OrdinalIgnoreCase);
        StopApps(keepPid);
        Directory.CreateDirectory(Paths.AppDir);
        if (!isInstalledCopy)
        {
            ReplaceAppExe(source);
            log($"Installed {Paths.AppExe}");
        }
        if (!engineOnly) await InstallDashboard(log, Path.GetDirectoryName(Path.GetFullPath(source))!);
        else if (File.Exists(Paths.DashboardExe)) RemoveDashboard(log);
        else log("The dashboard is not installed");
    }

    // A running exe cannot be overwritten but can be renamed, and the worker doing this may be running from that very
    // file. Earlier versions copied over it, which is why their tray could never update itself.
    static void ReplaceAppExe(string source)
    {
        var old = Paths.AppExe + ".old";
        try { File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (File.Exists(Paths.AppExe)) File.Move(Paths.AppExe, old, overwrite: true);
        try
        {
            File.Copy(source, Paths.AppExe);
        }
        catch
        {
            if (!File.Exists(Paths.AppExe)) File.Move(old, Paths.AppExe);
            throw;
        }
    }

    public static async Task SetDashboard(Action<string> log, bool installed, int keepPid)
    {
        StopApps(keepPid, dashboardOnly: true);
        if (installed) await InstallDashboard(log, Path.GetDirectoryName(Paths.Self)!);
        else RemoveDashboard(log);
    }

    // Uses Dockyard.Dashboard.zip next to the installer, or downloads the one from this release.
    static async Task InstallDashboard(Action<string> log, string sourceDir)
    {
        var zip = Path.Combine(sourceDir, Paths.DashboardZipName);
        string? downloaded = null;
        if (!File.Exists(zip))
        {
            log("Downloading the dashboard");
            zip = downloaded = await Updates.DownloadReleaseAsset(Paths.DashboardZipName, Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}"));
        }
        try
        {
            DashboardFiles.Extract(zip, Paths.AppDir);
            log($"Installed {Paths.DashboardExe}");
        }
        finally
        {
            if (downloaded is not null) Directory.Delete(Path.GetDirectoryName(downloaded)!, recursive: true);
        }
    }

    static void RemoveDashboard(Action<string> log)
    {
        DashboardFiles.Remove(Paths.AppDir, Path.GetFileName(Paths.DashboardExe), log);
        log("Removed the dashboard");
    }

    static void RemoveLegacy(Action<string> log, int keepPid)
    {
        if (!Directory.Exists(Paths.LegacyTrayDir)) return;
        foreach (var p in Process.GetProcessesByName(Paths.LegacyName)) Kill(p, keepPid);
        Directory.Delete(Paths.LegacyTrayDir, recursive: true);
        log($"Removed the old {Paths.LegacyName} from {Paths.LegacyTrayDir}");
    }

    static void StopApps(int keepPid, bool dashboardOnly = false)
    {
        var processes = Process.GetProcessesByName(Paths.DashboardName).AsEnumerable();
        if (!dashboardOnly) processes = processes.Concat(Process.GetProcessesByName(Paths.AppName));
        foreach (var p in processes)
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (string.Equals(file, Paths.AppExe, StringComparison.OrdinalIgnoreCase) || string.Equals(file, Paths.DashboardExe, StringComparison.OrdinalIgnoreCase)) Kill(p, keepPid);
            }
            catch { }
        }
    }

    static void Kill(Process p, int keepPid)
    {
        using (p)
        {
            if (p.Id == keepPid || p.Id == Environment.ProcessId) return;
            try
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            catch { }
        }
    }

    // dockerd refuses to register when its event source is left over, and "sc delete" leaves it. Without this,
    // installing again after an uninstall fails.
    static void RemoveEventSource() =>
        Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + Paths.ServiceName, throwOnMissingSubKey: false);

    static bool ServiceExists() => ServiceController.GetServices().Any(s => s.ServiceName == Paths.ServiceName);

    static ServiceControllerStatus ServiceStatus()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        return sc.Status;
    }

    static async Task StopService()
    {
        if (!ServiceExists()) return;
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)));
    }

    static void AddToMachinePath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        if (path.Split(';').Any(p => string.Equals(p.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))) return;
        Environment.SetEnvironmentVariable("Path", path.TrimEnd(';') + ";" + dir, EnvironmentVariableTarget.Machine);
    }

    static void RemoveFromMachinePath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var kept = path.Split(';').Where(p => !string.Equals(p.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("Path", string.Join(';', kept), EnvironmentVariableTarget.Machine);
    }
}

