using System.Diagnostics;
using Dockyard.Shared;

namespace Dockyard.Cli;

// The Dockyard Toolbox, which owns the engines: a command for it goes over its pipe when it runs, and otherwise to the
// Toolbox started for that one command.
static class Toolbox
{
    static string ExeName => OperatingSystem.IsWindows() ? "Dockyard.exe" : "Dockyard";

    // Next to this command (Linux, the macOS app bundle) or one folder up (Windows keeps the command in bin).
    public static string? Locate(string? baseDirectory = null)
    {
        var here = baseDirectory ?? AppContext.BaseDirectory;
        foreach (var dir in new[] { here, Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(here)) })
            if (dir is not null && Path.Combine(dir, ExeName) is var exe && File.Exists(exe)) return exe;
        return null;
    }

    public static int Run(IReadOnlyList<string> args, Action<string> output, Action<string> error)
    {
        args = Arguments.InlineFile(args);
        if (LocalPipe.RunInTray(args, output) is { } code) return code;
        return RunChild(args, output, error);
    }

    static int RunChild(IReadOnlyList<string> args, Action<string> output, Action<string> error)
    {
        var exe = Locate() ?? throw new InvalidOperationException("The Dockyard Toolbox is not installed next to this command; install Dockyard, then try again");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }

    // dockyard without a command opens the Toolbox; with the installer's options it installs, in this terminal.
    public static int HandOver(IReadOnlyList<string> args)
    {
        var exe = Locate() ?? throw new InvalidOperationException("The Dockyard Toolbox is not installed next to this command");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (args.Count == 0)
        {
            Process.Start(psi)?.Dispose();
            return 0;
        }
        return RunChild(args, Console.WriteLine, Console.Error.WriteLine);
    }
}
