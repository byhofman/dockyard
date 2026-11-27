namespace Dockyard.Core;

// The one rule for which terminal opens a shell pinned to a kube context: what each system has, in order of preference.
public static class ShellTerminal
{
    public enum Os { MacOS, Windows, Linux }

    public sealed record Command(string File, IReadOnlyList<string> Args);

    public static Os ThisSystem => OperatingSystem.IsMacOS() ? Os.MacOS : OperatingSystem.IsWindows() ? Os.Windows : Os.Linux;

    public static Command For(string context, Os system, Func<string, bool> onPath)
    {
        string[] ctx = ["dockyard", "ctx", context];
        switch (system)
        {
            case Os.MacOS:
                var line = $"dockyard ctx {Shell.ShQuote(context)}";
                var script = $"tell application \"Terminal\" to do script \"{line.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
                return new("osascript", ["-e", script, "-e", "tell application \"Terminal\" to activate"]);
            case Os.Windows:
                return onPath("wt")
                    ? new("wt", ctx)
                    : new("powershell", ["-NoExit", "-Command", $"dockyard ctx '{context.Replace("'", "''")}'"]);
            default:
                if (onPath("x-terminal-emulator")) return new("x-terminal-emulator", ["-e", .. ctx]);
                if (onPath("gnome-terminal")) return new("gnome-terminal", ["--", .. ctx]);
                throw new InvalidOperationException("No terminal found: install one that x-terminal-emulator or gnome-terminal starts");
        }
    }

    public static bool OnPath(string program)
    {
        var names = OperatingSystem.IsWindows() ? new[] { program + ".exe", program + ".cmd", program } : [program];
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(n => File.Exists(Path.Combine(dir, n))));
    }
}
