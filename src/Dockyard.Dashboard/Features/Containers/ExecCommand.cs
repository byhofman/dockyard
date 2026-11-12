namespace Dockyard.Dashboard.Features.Containers;

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
