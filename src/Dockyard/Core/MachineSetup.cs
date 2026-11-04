using System.Diagnostics;
using System.IO.Compression;
using System.ServiceProcess;
using System.Text.Json;
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
        var reboot = await EnableContainersFeature(log);
        await UpdateEngine(log, onlyIfNewer: true);
        AddToMachinePath(Paths.DockerDir);
        foreach (var plugin in Plugins) await UpdatePlugin(log, plugin.Component);
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
        if (ServiceExists())
        {
            log("Removing docker service");
            await StopService();
            await Shell.Check("sc.exe", "delete", Paths.ServiceName);
        }
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Remove-MpPreference -ControlledFolderAccessAllowedApplications '{Paths.DockerdExe}'");
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
        log($"Images and containers under {Path.GetDirectoryName(Paths.DaemonConfigDir)} were left in place");
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
                entry.ExtractToFile(target, overwrite: true);
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
        await Updates.DownloadFile(release.DownloadUrl!, Path.Combine(Paths.PluginDir, plugin.File));
        log($"{plugin.File} {release.Tag}");
    }

    static async Task ConfigureWindowsEngine(Action<string> log, string userSid, string userName)
    {
        if ((await Shell.Run("net.exe", "localgroup", "docker-users")).ExitCode != 0)
            await Shell.Check("net.exe", "localgroup", "docker-users", "/add", "/comment:Access to the Windows Docker engine");
        var add = await Shell.Run("net.exe", "localgroup", "docker-users", userName, "/add");
        if (add.ExitCode == 0) log($"Added {userName} to docker-users (applies after sign-out)");
        else if (!(add.Output + add.Error).Contains("1378"))
            throw new InvalidOperationException($"Adding {userName} to docker-users failed: {(add.Error + add.Output).Trim()}");

        // Controlled Folder Access otherwise blocks dockerd unpacking image layers ("hcsshim::ProcessBaseLayer ... Access is denied").
        // When Intune manages this list it may override the local entry; then it has to be added there.
        await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Add-MpPreference -ControlledFolderAccessAllowedApplications '{Paths.DockerdExe}'");

        Directory.CreateDirectory(Paths.DaemonConfigDir);
        File.WriteAllText(Path.Combine(Paths.DaemonConfigDir, "daemon.json"),
            JsonSerializer.Serialize(new DaemonConfig([Paths.WindowsHost], "docker-users"), DaemonConfigJson.Default.DaemonConfig));

        if (!ServiceExists())
        {
            log("Registering docker service");
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
    }

    static async Task InstallApp(Action<string> log, string source, int keepPid, bool engineOnly)
    {
        var isInstalledCopy = string.Equals(Path.GetFullPath(source), Paths.AppExe, StringComparison.OrdinalIgnoreCase);
        StopApps(keepPid);
        Directory.CreateDirectory(Paths.AppDir);
        if (!isInstalledCopy)
        {
            File.Copy(source, Paths.AppExe, overwrite: true);
            log($"Installed {Paths.AppExe}");
        }
        if (engineOnly) log("Engine-only install: the dashboard was not installed");
        else await InstallDashboard(log, Path.GetDirectoryName(Path.GetFullPath(source))!);
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
            var files = new List<string>();
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
                {
                    var target = Path.GetFullPath(Path.Combine(Paths.AppDir, entry.FullName));
                    if (!target.StartsWith(Paths.AppDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    files.Add(entry.FullName);
                }
            }
            File.WriteAllLines(Paths.DashboardManifest, files);
            log($"Installed {Paths.DashboardExe}");
        }
        finally
        {
            if (downloaded is not null) Directory.Delete(Path.GetDirectoryName(downloaded)!, recursive: true);
        }
    }

    static void RemoveDashboard(Action<string> log)
    {
        var files = File.Exists(Paths.DashboardManifest) ? File.ReadAllLines(Paths.DashboardManifest) : [Path.GetFileName(Paths.DashboardExe)];
        foreach (var file in files.Where(f => f.Length > 0))
        {
            var target = Path.GetFullPath(Path.Combine(Paths.AppDir, file));
            if (!target.StartsWith(Paths.AppDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(target); } catch (IOException) { log($"  in use, left behind: {target}"); }
        }
        File.Delete(Paths.DashboardManifest);
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

sealed record DaemonConfig(
    [property: System.Text.Json.Serialization.JsonPropertyName("hosts")] string[] Hosts,
    [property: System.Text.Json.Serialization.JsonPropertyName("group")] string Group);

[System.Text.Json.Serialization.JsonSerializable(typeof(DaemonConfig))]
partial class DaemonConfigJson : System.Text.Json.Serialization.JsonSerializerContext;
