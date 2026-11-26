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

    public static bool IsRunning => ProcessLock.IsHeld(ProcessLock.TrayFile);
}
