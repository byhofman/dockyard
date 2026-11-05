using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed record ProcessCommand(string File, IReadOnlyList<string> Args);

static class DockerCli
{
    // An app started from the menu bar or at login does not get the PATH of a shell, so on macOS "docker" alone is not found.
    public static string Path(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var own = System.IO.Path.Combine(home, ".dockyard", "bin", "docker");
        return !OperatingSystem.IsWindows() && File.Exists(own) ? own : "docker";
    }
}

enum TerminalKind { WindowsTerminal, WindowsConsole, Linux, MacTerminal }

sealed record TerminalApp(TerminalKind Kind, string Path)
{
    public static readonly TerminalApp WindowsConsole = new(TerminalKind.WindowsConsole, "docker");
    public static readonly TerminalApp MacTerminal = new(TerminalKind.MacTerminal, "osascript");
}

static class ExecCommand
{
    const string LinuxShell = "command -v bash >/dev/null && exec bash || exec sh";
    const string WindowsShell = "where powershell >nul 2>&1 && powershell || cmd";
    static readonly string[] LinuxTerminals = ["x-terminal-emulator", "gnome-terminal", "konsole", "kitty", "alacritty", "xfce4-terminal", "xterm"];

    public static ProcessCommand Build(string context, string id, string title, bool windowsContainer, TerminalApp terminal, string dockerCli = "docker")
    {
        List<string> docker = [dockerCli, "--context", context, "exec", "-it", id];
        docker.AddRange(windowsContainer ? ["cmd", "/c", WindowsShell] : ["sh", "-c", LinuxShell]);

        return terminal.Kind switch
        {
            TerminalKind.WindowsTerminal => new ProcessCommand(terminal.Path, ["-w", "new", "--title", title, "--", .. docker]),
            TerminalKind.WindowsConsole => new ProcessCommand("docker", docker[1..]),
            TerminalKind.MacTerminal => new ProcessCommand("osascript",
                ["-e", $"tell application \"Terminal\" to do script \"{AppleScriptEscape(ShellJoin(docker))}\"", "-e", "tell application \"Terminal\" to activate"]),
            _ => Path.GetFileName(terminal.Path) switch
            {
                "gnome-terminal" => new ProcessCommand(terminal.Path, ["--title", title, "--", .. docker]),
                "kitty" => new ProcessCommand(terminal.Path, ["--title", title, .. docker]),
                "xfce4-terminal" => new ProcessCommand(terminal.Path, ["--title", title, "-x", .. docker]),
                _ => new ProcessCommand(terminal.Path, ["-e", .. docker]),
            },
        };
    }

    public static TerminalApp? FindTerminal()
    {
        if (OperatingSystem.IsWindows())
        {
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            return File.Exists(wt) ? new TerminalApp(TerminalKind.WindowsTerminal, wt) : TerminalApp.WindowsConsole;
        }
        if (OperatingSystem.IsMacOS()) return TerminalApp.MacTerminal;

        var candidates = new[] { Environment.GetEnvironmentVariable("TERMINAL") }.Concat(LinuxTerminals).Where(c => !string.IsNullOrEmpty(c));
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var candidate in candidates)
        {
            var full = Path.IsPathRooted(candidate!) ? candidate! : path.Select(p => Path.Combine(p, candidate!)).FirstOrDefault(File.Exists);
            if (full is not null && File.Exists(full)) return new TerminalApp(TerminalKind.Linux, full);
        }
        return null;
    }

    static string ShellJoin(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/') ? a : "'" + a.Replace("'", @"'\''") + "'"));

    static string AppleScriptEscape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

sealed class LogBuffer(int capacity = 10_000)
{
    readonly Queue<LogLine> lines = new();

    public int Capacity { get; } = capacity;
    public int Count => lines.Count;
    public IEnumerable<LogLine> Lines => lines;

    public bool Add(LogLine line)
    {
        var dropped = false;
        if (lines.Count == Capacity)
        {
            lines.Dequeue();
            dropped = true;
        }
        lines.Enqueue(line);
        return dropped;
    }

    public void Clear() => lines.Clear();

    public List<LogLine> Filtered(string? search) => string.IsNullOrEmpty(search)
        ? lines.ToList()
        : lines.Where(l => Matches(l, search)).ToList();

    public static bool Matches(LogLine line, string? search) =>
        string.IsNullOrEmpty(search) || line.Text.Contains(search, StringComparison.OrdinalIgnoreCase);
}

static class StatsSelection
{
    public const int Limit = 50;

    public static HashSet<string> Choose(IReadOnlyCollection<string> running, IReadOnlySet<string> visible, int limit = Limit) =>
        running.Count <= limit ? running.ToHashSet() : running.Where(visible.Contains).ToHashSet();
}
