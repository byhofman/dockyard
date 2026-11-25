using System.Diagnostics;
using Dockyard.Shared;

namespace Dockyard.Dashboard.Shell;

static class TrayClient
{
    // The Toolbox, by its name since 0.9.0 or the one before.
    static string TrayExe => (OperatingSystem.IsWindows() ? new[] { "dockyard-toolbox.exe", "Dockyard.exe" } : ["dockyard-toolbox", "Dockyard"])
        .Select(n => Path.Combine(AppContext.BaseDirectory, n)).FirstOrDefault(File.Exists) ?? Path.Combine(AppContext.BaseDirectory, "dockyard-toolbox");

    public static bool Send(TrayCommand command)
    {
        if (LocalPipe.TrySend(LocalPipe.TrayName, command.ToString())) return true;
        if (!File.Exists(TrayExe)) return false;

        string[] args = command.Kind switch
        {
            TrayCommandKind.Settings => ["--tray", "--settings"],
            TrayCommandKind.Start => ["--tray", "--start", command.Engine!],
            TrayCommandKind.Switch => ["--tray", "--switch", command.Engine!],
            _ => ["--tray"],
        };
        try
        {
            Process.Start(new ProcessStartInfo(TrayExe, args) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
