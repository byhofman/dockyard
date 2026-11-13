using System.ComponentModel;
using System.Diagnostics;
using Dockyard.Hosts;

namespace Dockyard.Core;

// Dockyard, its dashboard and the tools next to docker in Program Files, with the Defender exclusions and the PATH entry
// they need. Runs elevated.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class MachineApps
{
    public static readonly (Component Component, string File, Release Release)[] Plugins =
    [
        (Component.Compose, "docker-compose.exe", Updates.WindowsCompose),
        (Component.Buildx, "docker-buildx.exe", Updates.WindowsBuildx),
    ];

    public static async Task UpdatePlugin(Action<string> log, Component component)
    {
        var plugin = Plugins.Single(p => p.Component == component);
        Directory.CreateDirectory(Paths.PluginDir);
        var target = Path.Combine(Paths.PluginDir, plugin.File);
        var incoming = target + ".new";
        await Pinned.Download(plugin.Release, incoming);
        InUseFiles.MoveAside(target);
        File.Move(incoming, target);
        log($"{plugin.File} {plugin.Release.Tag}");
    }

    public static void RemovePlugin(Action<string> log, Component component)
    {
        var plugin = Plugins.Single(p => p.Component == component);
        var target = Path.Combine(Paths.PluginDir, plugin.File);
        if (!File.Exists(target)) return;
        InUseFiles.MoveAside(target);
        log($"Removed {plugin.File}");
    }

    public static async Task InstallKind(Action<string> log)
    {
        Directory.CreateDirectory(Paths.DockerDir);
        var incoming = Paths.KindExe + ".new";
        await Kind.Download(incoming);
        InUseFiles.MoveAside(Paths.KindExe);
        File.Move(incoming, Paths.KindExe);
        log($"kind {Kind.Tag}");
    }

    public static void RemoveKind(Action<string> log)
    {
        if (!File.Exists(Paths.KindExe)) return;
        InUseFiles.MoveAside(Paths.KindExe);
        log("Removed kind");
    }

    // Not worth failing an install over: without it docker login just keeps working the way it did.
    public static async Task InstallCredentialHelper(Action<string> log)
    {
        if (File.Exists(Paths.CredentialHelper)) return;
        try
        {
            var release = Pinned.CredentialHelper("wincred", "windows-amd64.exe");
            await Pinned.Download(release, Paths.CredentialHelper);
            log($"Credential helper {release.Tag}");
        }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or IOException)
        {
            log($"The credential helper was not installed ({e.Message}); running the wizard again retries it");
        }
    }

    // Only files in Program Files, which only administrators can write: an exclusion goes by path and exempts whatever is
    // there from every ASR rule. Earlier versions also excluded the installer wherever it ran, and the one-liner asks for
    // its own download path when Defender blocks it, so uninstall takes those back too.
    static IEnumerable<string> ExcludedFromAsr => new[] { Paths.AppExe, Paths.DashboardExe };
    public static IEnumerable<string> EverExcludedFromAsr => ExcludedFromAsr.Append(Paths.LegacyDashboardExe).Append(Paths.OneLinerExe);

    // Defender's prevalence/age rule (event 1121) blocks every new build, and an exclusion goes by path because a hash changes with each build.
    public static async Task AddDefenderExclusions(Action<string> log)
    {
        var paths = ExcludedFromAsr.Select(Shell.PsQuote);
        var r = await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Add-MpPreference -AttackSurfaceReductionOnlyExclusions {string.Join(',', paths)}");
        log(r.ExitCode == 0 ? "Defender: Dockyard is excluded from the prevalence rule" : "Defender exclusions were not added (managed device?); ask your administrator if Defender blocks Dockyard");
    }

    public static async Task InstallApp(Action<string> log, string source, int keepPid, bool engineOnly, string? dashboardDir)
    {
        var isInstalledCopy = string.Equals(Path.GetFullPath(source), Paths.AppExe, StringComparison.OrdinalIgnoreCase);
        StopApps(keepPid);
        Directory.CreateDirectory(Paths.AppDir);
        if (!isInstalledCopy)
        {
            ReplaceAppExe(source);
            log($"Installed {Paths.AppExe}");
        }
        if (!engineOnly) await InstallDashboard(log, dashboardDir);
        else if (File.Exists(Paths.DashboardExe) || File.Exists(Paths.LegacyDashboardExe)) RemoveDashboard(log);
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

    // Dockyard.Dashboard.zip from the given folder, or the one from this release. A download goes to Program Files, which
    // only administrators can write, so nothing can swap it between the check and the extraction.
    public static async Task InstallDashboard(Action<string> log, string? sourceDir)
    {
        var zip = sourceDir is null ? null : Path.Combine(sourceDir, Paths.DashboardZipName);
        string? downloaded = null;
        if (zip is null || !File.Exists(zip))
        {
            log("Downloading the dashboard");
            zip = downloaded = await Updates.DownloadReleaseAsset(Paths.DashboardZipName, Path.Combine(Paths.AppDir, $".download-{Guid.NewGuid():N}"));
        }
        try
        {
            DashboardFiles.Extract(zip, Paths.AppDir, Path.GetFileName(Paths.LegacyDashboardExe));
            log($"Installed {Paths.DashboardExe}");
        }
        finally
        {
            if (downloaded is not null) Directory.Delete(Path.GetDirectoryName(downloaded)!, recursive: true);
        }
    }

    public static void RemoveDashboard(Action<string> log)
    {
        DashboardFiles.Remove(Paths.AppDir, [Path.GetFileName(Paths.DashboardExe), Path.GetFileName(Paths.LegacyDashboardExe)], log);
        log("Removed the dashboard");
    }

    public static void RemoveLegacy(Action<string> log, int keepPid)
    {
        if (!Directory.Exists(Paths.LegacyTrayDir)) return;
        foreach (var p in Process.GetProcessesByName(Paths.LegacyName)) Kill(p, keepPid);
        Directory.Delete(Paths.LegacyTrayDir, recursive: true);
        log($"Removed the old {Paths.LegacyName} from {Paths.LegacyTrayDir}");
    }

    public static void StopApps(int keepPid, bool dashboardOnly = false)
    {
        var processes = Process.GetProcessesByName(Paths.DashboardProcess).Concat(Process.GetProcessesByName(Paths.LegacyDashboardProcess));
        if (!dashboardOnly) processes = processes.Concat(Process.GetProcessesByName(Paths.AppName));
        foreach (var p in processes)
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (new[] { Paths.AppExe, Paths.DashboardExe, Paths.LegacyDashboardExe }.Contains(file, StringComparer.OrdinalIgnoreCase)) Kill(p, keepPid);
            }
            // Gone by now, or another user's process whose file cannot be read: not one of ours to stop.
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
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
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
        }
    }

    public static void AddToMachinePath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        if (path.Split(';').Any(p => string.Equals(p.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))) return;
        Environment.SetEnvironmentVariable("Path", path.TrimEnd(';') + ";" + dir, EnvironmentVariableTarget.Machine);
    }

    public static void RemoveFromMachinePath(string dir)
    {
        var path = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine) ?? "";
        var kept = path.Split(';').Where(p => !string.Equals(p.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase));
        Environment.SetEnvironmentVariable("Path", string.Join(';', kept), EnvironmentVariableTarget.Machine);
    }
}
