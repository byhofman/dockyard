using System.Runtime.InteropServices;

namespace Dockyard.Core;

// Whether a person can be asked here, for setup's choices and for sudo's password alike: both read the terminal that the
// input and the output are. The one-liners hand setup the terminal when they are piped into sh.
static partial class Terminal
{
    public static bool IsAttached => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    const int ParentProcess = -1;

    public static void AttachParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        // The prompt the parent printed is on the line the output would otherwise start on.
        if (AttachConsole(ParentProcess)) Console.WriteLine();
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);
}

// Windows and macOS have a desktop for any interactive session that is not over SSH; Linux has one when a display is set.
static class Session
{
    public static bool HasDesktop() => HasDesktop(
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        Environment.UserInteractive,
        Environment.GetEnvironmentVariable);

    public static bool HasDesktop(OSPlatform os, bool interactive, Func<string, string?> environment)
    {
        if (!interactive) return false;
        if (os == OSPlatform.Linux) return !string.IsNullOrEmpty(environment("DISPLAY")) || !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"));
        return string.IsNullOrEmpty(environment("SSH_CONNECTION"));
    }
}
