using System.IO.Compression;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using Dockyard.Hosts;

namespace Dockyard.Core;

// The Windows engine and the docker CLI in Program Files: the Containers feature, dockerd's files, its service and its
// daemon.json. Runs elevated.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class MachineEngine
{
    public static async Task<bool> EnableContainersFeature(Action<string> log)
    {
        var info = await Shell.Check("dism.exe", "/online", "/get-featureinfo", "/featurename:Containers", "/English");
        if (Regex.IsMatch(info, @"State : Enabled")) { log("Containers feature already enabled"); return false; }

        log("Enabling Windows Containers feature");
        var r = await Shell.Run("dism.exe", "/online", "/enable-feature", "/featurename:Containers", "/all", "/norestart", "/quiet");
        if (r.ExitCode == 3010) { log("Containers feature enabled (reboot required)"); return true; }
        if (r.ExitCode != 0) throw new InvalidOperationException($"Enabling Containers feature failed ({r.ExitCode}): {r.Output.Trim()}");
        return false;
    }

    public static async Task Update(Action<string> log, bool onlyIfNewer)
    {
        var latest = Updates.WindowsDocker;
        var current = await Updates.Installed(Component.Engine);
        if (onlyIfNewer && current >= latest.Version) { log($"Docker {current} is up to date"); return; }

        log($"Installing Docker {latest.Version}" + (current is null ? "" : $" (was {current})"));
        // Downloaded first, so a failed download leaves the engine running as it was.
        var zip = await DownloadDockerZip(latest);
        var wasRunning = ServiceExists() && ServiceStatus() == ServiceControllerStatus.Running;
        await StopService();

        var root = Path.GetDirectoryName(Paths.DockerDir)!;
        ExtractDockerZip(zip, archive =>
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));
                if (!target.StartsWith(Paths.DockerDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"The Docker archive has an entry outside {Paths.DockerDir}: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                InUseFiles.MoveAside(target);
                entry.ExtractToFile(target);
            }
        });

        if (wasRunning)
        {
            using var sc = new ServiceController(Paths.ServiceName);
            sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)));
        }
    }

    // The CLI on its own, for when the Windows engine that normally brings it is not wanted.
    public static async Task UpdateCli(Action<string> log, bool onlyIfNewer)
    {
        var latest = Updates.WindowsDocker;
        var current = await Updates.Installed(Component.Cli);
        if (onlyIfNewer && current >= latest.Version) return;
        log($"Installing the Docker CLI {latest.Version}");
        ExtractDockerZip(await DownloadDockerZip(latest), archive =>
        {
            var entry = archive.Entries.First(e => e.Name.Equals("docker.exe", StringComparison.OrdinalIgnoreCase));
            InUseFiles.MoveAside(Paths.DockerExe);
            entry.ExtractToFile(Paths.DockerExe);
        });
    }

    // Into Program Files, which only administrators can write: in this user's temp folder a process without elevation
    // could swap the checked zip before this elevated one extracts it.
    static async Task<string> DownloadDockerZip(Release release)
    {
        Directory.CreateDirectory(Paths.DockerDir);
        var zip = Path.Combine(Paths.DockerDir, $"docker-{release.Version}.zip");
        await Pinned.Download(release, zip);
        return zip;
    }

    static void ExtractDockerZip(string zip, Action<ZipArchive> extract)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zip);
            extract(archive);
        }
        finally
        {
            File.Delete(zip);
        }
    }

    // The CLI and the plugins stay, and so do the engine's images under ProgramData: adding it back finds them there.
    public static async Task Remove(Action<string> log)
    {
        await RemoveService();
        if (!Directory.Exists(Paths.DockerDir)) return;
        var removed = false;
        foreach (var file in Directory.GetFiles(Paths.DockerDir))
        {
            if (WindowsEngineFiles.Keep(Path.GetFileName(file))) continue;
            InUseFiles.MoveAside(file);
            removed = true;
        }
        if (removed) log("Removed the Windows engine; its images are kept");
    }

    public static async Task RemoveService(Action<string>? log = null)
    {
        if (!ServiceExists()) return;
        log?.Invoke("Removing docker service");
        await StopService();
        await Shell.Check("sc.exe", "delete", Paths.ServiceName);
        RemoveEventSource();
    }

    public static async Task Configure(Action<string> log, string userSid, string userName)
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
            $"Add-MpPreference -ControlledFolderAccessAllowedApplications {Shell.PsQuote(Paths.DockerdExe)}");

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
}
