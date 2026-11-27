using System.Runtime.Versioning;

namespace Dockyard.Core;

// Windows Search and the Start menu list an app by its shortcut in Start Menu\Programs; the files under Program Files are
// only files to them. Setup makes it and uninstall takes it out. The dashboard and the tray are compiled ahead of time,
// without COM interop, so WScript.Shell writes the .lnk.
static class StartMenu
{
    public const string Description = "Docker without Docker Desktop";

    // The dashboard where there is one, else the tray; with neither there is nothing to open.
    public static string? Target(bool dashboard, bool tray, string dashboardExe, string trayExe) => dashboard ? dashboardExe : tray ? trayExe : null;

    public static string Script(string shortcut, string target) =>
        $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Quote(shortcut)}); $s.TargetPath = {Quote(target)}; "
        + $"$s.WorkingDirectory = {Quote(Path.GetDirectoryName(target) ?? "")}; $s.Description = {Quote(Description)}; $s.Save()";

    static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    [SupportedOSPlatform("windows")]
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Paths.AppName + ".lnk");

    [SupportedOSPlatform("windows")]
    public static async Task Place(Action<string> log)
    {
        if (Target(Paths.HasDashboard, File.Exists(Paths.AppExe), Paths.DashboardExe, Paths.AppExe) is not { } target)
        {
            Remove(log);
            return;
        }
        var r = await Shell.Run("powershell.exe", "-NoProfile", "-NonInteractive", "-Command", Script(Shortcut, target));
        log(r.ExitCode == 0 ? $"Dockyard is in the Start menu and Windows Search ({Path.GetFileName(target)})"
            : $"Dockyard could not be added to the Start menu: {r.Error.Trim()}");
    }

    [SupportedOSPlatform("windows")]
    public static void Remove(Action<string> log)
    {
        if (!File.Exists(Shortcut)) return;
        File.Delete(Shortcut);
        log("Took Dockyard out of the Start menu");
    }
}
