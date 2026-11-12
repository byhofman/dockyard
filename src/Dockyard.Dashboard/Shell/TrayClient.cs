using System.Diagnostics;
using Dockyard.Shared;

namespace Dockyard.Dashboard.Shell;

static class TrayClient
{
    static string TrayExe => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Dockyard.exe" : "Dockyard");

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
