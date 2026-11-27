using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
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

    public static async Task InstallTool(Action<string> log, CliTool tool)
    {
        Directory.CreateDirectory(Paths.DockerDir);
        var target = Paths.ToolExe(tool);
        var incoming = target + ".new";
        await Tools.Download(tool, incoming);
        InUseFiles.MoveAside(target);
        File.Move(incoming, target);
        log($"{tool.Command} {Tools.Pinned(tool).Tag}");
    }

    public static void RemoveTool(Action<string> log, CliTool tool)
    {
        var target = Paths.ToolExe(tool);
        if (!File.Exists(target)) return;
        InUseFiles.MoveAside(target);
        log($"Removed Dockyard's own {tool.Command} from {Paths.DockerDir}");
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
            log($"The credential helper was not installed ({e.Message}); running dockyard setup again retries it");
        }
    }

    // Only files in Program Files, which only administrators can write: an exclusion goes by path and exempts whatever is
    // there from every ASR rule. The one-liner asks for its own download path when Defender blocks it, so uninstall takes
    // that back too.
    static IEnumerable<string> ExcludedFromAsr => new[] { Paths.AppExe, Paths.DashboardExe, Paths.HostExe, Paths.DockyardCliExe };
    public static IEnumerable<string> EverExcludedFromAsr => ExcludedFromAsr.Append(Paths.OneLinerExe).Concat(WingetToolFiles);

    // Defender's prevalence/age rule (event 1121) blocks every new build, and an exclusion goes by path because a hash changes with each build.
    public static async Task AddDefenderExclusions(Action<string> log)
    {
        var paths = ExcludedFromAsr.Select(Shell.PsQuote);
        var r = await Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Add-MpPreference -AttackSurfaceReductionOnlyExclusions {string.Join(',', paths)}");
        log(r.ExitCode == 0 ? "Defender: Dockyard is excluded from the prevalence rule" : "Defender exclusions were not added (managed device?); ask your administrator if Defender blocks Dockyard");
    }

    // winget's tools in Program Files: a new release would not run until Defender found it common.
    public static void ExcludeFromAsr(IReadOnlyList<string> files)
    {
        var inProgramFiles = files.Where(f => Path.GetFullPath(f).StartsWith(Hosts.Winget.MachinePackages + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList();
        if (inProgramFiles.Count == 0) return;
        Shell.Run("powershell.exe", "-NoProfile", "-Command",
            $"Add-MpPreference -AttackSurfaceReductionOnlyExclusions {string.Join(',', inProgramFiles.Select(Shell.PsQuote))}").GetAwaiter().GetResult();
    }

    public static IEnumerable<string> WingetToolFiles => Tools.All.SelectMany(Hosts.Winget.MachineFiles);

    // Dockyard.Tray.zip, the tray with its native libraries, from the file given, or from the folder --assets names or the
    // tray's own release.
    public static async Task InstallTray(Action<string> log, string? zip = null)
    {
        Directory.CreateDirectory(Paths.AppDir);
        var download = Path.Combine(Paths.AppDir, $".download-{Guid.NewGuid():N}");
        try
        {
            if (zip is null) log("Downloading the Dockyard tray");
            zip ??= await Updates.DownloadComponentAsset(Updates.TrayComponent, Updates.TrayZipPattern, download);
            AppFiles.Extract(zip, Paths.AppDir, AppFiles.TrayManifest);
            log($"Installed {Paths.AppExe}");
        }
        finally
        {
            if (Directory.Exists(download)) Directory.Delete(download, recursive: true);
        }
    }

    public static void RemoveTray(Action<string> log)
    {
        AppFiles.Remove(Paths.AppDir, AppFiles.TrayManifest, log);
        log($"Removed {Paths.AppExe}");
    }

    // dockyard-host, which keeps the Linux engine running and reachable from Windows; the dockyard command carries it. A running
    // one is stopped to be replaced: the engine keeps running, and the new one takes it on.
    public static void InstallHost(Action<string> log, int keepPid)
    {
        using var embedded = typeof(Paths).Assembly.GetManifestResourceStream("dockyard-host.exe");
        if (embedded is null) return;
        foreach (var p in Process.GetProcessesByName(Paths.HostProcess)) Kill(p, keepPid);
        var incoming = Paths.HostExe + ".new";
        using (var file = File.Create(incoming)) embedded.CopyTo(file);
        InUseFiles.MoveAside(Paths.HostExe);
        File.Move(incoming, Paths.HostExe);
        log($"Installed {Paths.HostExe}");
    }

    // The dockyard command: a console program, so cmd and PowerShell wait for it and get its output. In a folder of its own,
    // where it is the only dockyard on the PATH. A failed download fails the setup that chose it.
    public static async Task InstallCli(Action<string> log, string? file)
    {
        var download = Path.Combine(Paths.AppDir, $".download-{Guid.NewGuid():N}");
        try
        {
            if (file is null)
            {
                log("Downloading the dockyard command");
                file = await Updates.DownloadComponentAsset(Updates.CliComponent, Updates.CliAssetPattern, download);
            }
            if (!string.Equals(Path.GetFullPath(file), Paths.DockyardCliExe, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(Paths.CommandDir);
                var incoming = Paths.DockyardCliExe + ".new";
                File.Copy(file, incoming, overwrite: true);
                InUseFiles.MoveAside(Paths.DockyardCliExe);
                File.Move(incoming, Paths.DockyardCliExe);
            }
        }
        finally
        {
            if (Directory.Exists(download)) Directory.Delete(download, recursive: true);
        }
        log($"Installed {Paths.DockyardCliExe}");
        AddToMachinePath(Paths.CommandDir);
    }

    // Moved aside, not deleted: it may be the very program that asks, and Windows lets a running program be renamed only.
    public static void RemoveCli(Action<string> log)
    {
        RemoveFromMachinePath(Paths.CommandDir);
        if (!File.Exists(Paths.DockyardCliExe)) return;
        InUseFiles.MoveAside(Paths.DockyardCliExe);
        log($"Removed {Paths.DockyardCliExe}");
    }

    // The dockyard command that runs setup is the one installed, as on macOS and Linux: DOCKYARD_VERSION pins it, and a
    // first install needs no download for it. From the tray or the dashboard, which are not it, the folder --assets names
    // or else the CLI's own release.
    public static string? CliFile(string? runningCli, string? assetsDir) =>
        runningCli ?? (assetsDir is null ? null : Directory.EnumerateFiles(assetsDir).FirstOrDefault(f => Regex.IsMatch(Path.GetFileName(f), Updates.CliAssetPattern)));

    // Dockyard.Dashboard.zip from the given folder, or the one from this release. A download goes to Program Files, which
    // only administrators can write, so nothing can swap it between the check and the extraction.
    public static async Task InstallDashboard(Action<string> log, string? sourceDir)
    {
        var zip = sourceDir is null ? null : Path.Combine(sourceDir, Paths.DashboardZipName);
        string? downloaded = null;
        if (zip is null || !File.Exists(zip))
        {
            log("Downloading the dashboard");
            zip = downloaded = await Updates.DownloadComponentAsset(Updates.DashboardComponent, Updates.DashboardZipPattern, Path.Combine(Paths.AppDir, $".download-{Guid.NewGuid():N}"));
        }
        try
        {
            AppFiles.Extract(zip, Paths.AppDir, AppFiles.DashboardManifest);
            log($"Installed {Paths.DashboardExe}");
        }
        finally
        {
            if (downloaded is not null) Directory.Delete(Path.GetDirectoryName(downloaded)!, recursive: true);
        }
    }

    public static void RemoveDashboard(Action<string> log)
    {
        AppFiles.Remove(Paths.AppDir, AppFiles.DashboardManifest, log);
        log("Removed the dashboard");
    }

    public static void StopApps(int keepPid, bool dashboardOnly = false, bool trayOnly = false)
    {
        IEnumerable<Process> processes = [];
        if (!trayOnly) processes = processes.Concat(Process.GetProcessesByName(Paths.DashboardProcess));
        if (!dashboardOnly) processes = processes.Concat(Process.GetProcessesByName(Paths.AppProcess));
        foreach (var p in processes)
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (new[] { Paths.DashboardExe, Paths.AppExe }.Contains(file, StringComparer.OrdinalIgnoreCase)) Kill(p, keepPid);
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
