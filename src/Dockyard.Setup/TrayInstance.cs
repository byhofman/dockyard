namespace Dockyard.Setup;

// One tray per user.
public static class TrayInstance
{
    // Held until the process ends. Null when another tray holds it. A few tries: the dashboard looks at the lock by taking it
    // for a moment.
    public static IDisposable? TryTake()
    {
        for (var attempt = 0; ; attempt++)
        {
            if (ProcessLock.TryTake(ProcessLock.TrayFile) is { } taken) return taken;
            if (attempt == 4) return null;
            Thread.Sleep(100);
        }
    }

    // Closing the dashboard's window hides it only while a tray can bring it back or end it.
    public static bool ClosingHidesDashboard(bool trayRunning) => trayRunning;
    public static bool ClosingHidesDashboard() => ClosingHidesDashboard(IsRunning);

    public static bool IsRunning => ProcessLock.IsHeld(ProcessLock.TrayFile);

    // On macOS someone who opens Dockyard.app from Finder or Spotlight wants the dashboard, where there is one; the login
    // item starts only the tray. launchd names the login item's job in XPC_SERVICE_NAME, and an application otherwise.
    public static bool OpensDashboardAtStart(string? xpcServiceName) => xpcServiceName != Hosts.MacLayout.LaunchAgentLabel;
}
