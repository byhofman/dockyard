using System.Diagnostics;

namespace Dockyard.Dashboard.Shell;

// DOCKYARD_TRACE_STARTUP=1 prints milliseconds since process start for each startup phase to stderr.
static class StartupTrace
{
    static readonly bool Enabled = Environment.GetEnvironmentVariable("DOCKYARD_TRACE_STARTUP") == "1";

    public static void Mark(string phase)
    {
        if (!Enabled) return;
        using var process = Process.GetCurrentProcess();
        Console.Error.WriteLine($"startup {(DateTime.Now - process.StartTime).TotalMilliseconds,6:0} ms  {phase}");
    }
}
