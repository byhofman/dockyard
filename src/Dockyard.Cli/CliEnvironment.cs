using System.Diagnostics;
using System.Runtime.InteropServices;
using Dockyard.Setup;
using Dockyard.Shared;
using Spectre.Console;

namespace Dockyard.Cli;

interface IDashboardLauncher
{
    void Open(string? dashboardExe, string section);
}

// What a command runs against: the library, the terminal and the session it runs in. The tests bring their own.
sealed record CliEnvironment(Func<DockyardSetup> Setup, IAnsiConsole Console, TextWriter Error, bool HasTerminal, bool HasDesktop, IDashboardLauncher Dashboard)
{
    public static CliEnvironment ForThisMachine() => new(
        DockyardSetup.ForThisMachine,
        AnsiConsole.Console,
        System.Console.Error,
        HasTerminal: !System.Console.IsInputRedirected && !System.Console.IsOutputRedirected,
        Session.HasDesktop(),
        new SystemDashboardLauncher());
}

static class Session
{
    public static bool HasDesktop() => HasDesktop(
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        Environment.UserInteractive,
        Environment.GetEnvironmentVariable);

    // Windows and macOS have a desktop for any interactive session that is not over SSH; Linux has one when a display is set.
    public static bool HasDesktop(OSPlatform os, bool interactive, Func<string, string?> environment)
    {
        if (!interactive) return false;
        if (os == OSPlatform.Linux) return !string.IsNullOrEmpty(environment("DISPLAY")) || !string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"));
        return string.IsNullOrEmpty(environment("SSH_CONNECTION"));
    }
}

sealed class SystemDashboardLauncher : IDashboardLauncher
{
    public void Open(string? dashboardExe, string section)
    {
        if (LocalPipe.TrySend(LocalPipe.DashboardName, $"show {section}")) return;
        if (dashboardExe is null || !File.Exists(dashboardExe)) return;
        try
        {
            Process.Start(new ProcessStartInfo(dashboardExe, ["--section", section]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            System.Console.Error.WriteLine($"The dashboard could not be opened: {e.Message}");
        }
    }
}
