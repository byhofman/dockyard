using System.Diagnostics;
using Dockyard.Setup;
using Dockyard.Shared;
using Spectre.Console;

namespace Dockyard.Cli;

interface IDashboardLauncher
{
    void Open(string? dashboardExe, string section);
}

// What a command runs against: the library, the terminal and the session it runs in. The tests bring their own.
sealed record CliEnvironment(Func<DockyardSetup> Setup, IAnsiConsole Console, TextWriter Error, bool HasTerminal, bool HasDesktop, IDashboardLauncher Dashboard, IShellLauncher Shell)
{
    public static CliEnvironment ForThisMachine() => new(
        DockyardSetup.ForThisMachine,
        AnsiConsole.Console,
        System.Console.Error,
        DockyardSetup.HasTerminal,
        DockyardSetup.HasDesktop,
        new SystemDashboardLauncher(),
        new SystemShellLauncher());
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

interface IShellLauncher
{
    // Starts the person's shell on the kubeconfig, waits for it and gives its exit code.
    int Run(string kubeconfig, string context);
}

sealed class SystemShellLauncher : IShellLauncher
{
    public int Run(string kubeconfig, string context)
    {
        var start = new ProcessStartInfo(ShellFile()) { UseShellExecute = false };
        start.Environment["KUBECONFIG"] = kubeconfig;
        start.Environment["DOCKYARD_CONTEXT"] = context;
        // Ctrl+C goes to the whole foreground group: the shell takes it, and this command stays to delete the file.
        ConsoleCancelEventHandler ignore = (_, e) => e.Cancel = true;
        System.Console.CancelKeyPress += ignore;
        try
        {
            using var shell = Process.Start(start) ?? throw new InvalidOperationException("The shell did not start");
            shell.WaitForExit();
            return shell.ExitCode;
        }
        finally { System.Console.CancelKeyPress -= ignore; }
    }

    // The parent shell would be the better choice on Windows, but finding it takes native calls; pwsh and powershell are always there to fall back on.
    static string ShellFile() =>
        OperatingSystem.IsWindows()
            ? Dockyard.Core.ShellTerminal.OnPath("pwsh") ? "pwsh" : "powershell"
            : Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } shell ? shell : "/bin/sh";
}
