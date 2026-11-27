namespace Dockyard.Core;

// The one rule for which terminal runs a dockyard command, such as a shell pinned to a kube context: what each system has,
// in order of preference.
public static class ShellTerminal
{
    public enum Os { MacOS, Windows, Linux }

    public sealed record Command(string File, IReadOnlyList<string> Args);

    public static Os ThisSystem => OperatingSystem.IsMacOS() ? Os.MacOS : OperatingSystem.IsWindows() ? Os.Windows : Os.Linux;

    public static Command For(string context, Os system, Func<string, bool> onPath) => Running(["ctx", context], system, onPath);

    public static Command Running(IReadOnlyList<string> args, Os system, Func<string, bool> onPath)
    {
        string[] command = ["dockyard", .. args];
        switch (system)
        {
            case Os.MacOS:
                var line = string.Join(' ', command.Select(a => IsPlain(a) ? a : Shell.ShQuote(a)));
                var script = $"tell application \"Terminal\" to do script \"{line.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
                return new("osascript", ["-e", script, "-e", "tell application \"Terminal\" to activate"]);
            case Os.Windows:
                return onPath("wt")
                    ? new("wt", command)
                    : new("powershell", ["-NoExit", "-Command", string.Join(' ', command.Select(a => IsPlain(a) ? a : $"'{a.Replace("'", "''")}'"))]);
            default:
                if (onPath("x-terminal-emulator")) return new("x-terminal-emulator", ["-e", .. command]);
                if (onPath("gnome-terminal")) return new("gnome-terminal", ["--", .. command]);
                throw new InvalidOperationException("No terminal found: install one that x-terminal-emulator or gnome-terminal starts");
        }
    }

    static bool IsPlain(string arg) => arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/');

    public static bool OnPath(string program)
    {
        var names = OperatingSystem.IsWindows() ? new[] { program + ".exe", program + ".cmd", program } : [program];
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(n => File.Exists(Path.Combine(dir, n))));
    }
}
