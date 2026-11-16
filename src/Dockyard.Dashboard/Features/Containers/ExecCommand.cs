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
        return InTerminal(docker, title, terminal);
    }

    // Attaches to a container made for this terminal alone. Closing a terminal does not end what runs in the container,
    // so a watcher that outlives the window removes it once the terminal's process is gone, however it ended.
    public static ProcessCommand BuildImageTerminal(string context, string id, string title, TerminalApp terminal, string dockerCli = "docker") =>
        InTerminal(ImageTerminalCommand(context, id, terminal.Kind is TerminalKind.WindowsTerminal or TerminalKind.WindowsConsole, dockerCli), title, terminal);

    public static List<string> ImageTerminalCommand(string context, string id, bool windows, string dockerCli)
    {
        if (!windows)
        {
            // $$ is this shell, which becomes docker by exec; the watcher ignores the hangup that closing the window sends.
            const string script = "( trap '' HUP INT TERM; while kill -0 $$ 2>/dev/null; do sleep 1; done; \"$1\" --context \"$2\" rm -f \"$3\" ) >/dev/null 2>&1 </dev/null & exec \"$1\" --context \"$2\" start -ai \"$3\"";
            return ["sh", "-c", script, "dockyard-terminal", dockerCli, context, id];
        }
        var docker = $"& {Quote(dockerCli)} --context {Quote(context)}";
        // A hidden PowerShell of its own, so closing the window does not take it along; it waits for the one that started it.
        var watcher = Encode($"""
            $parent = (Get-CimInstance Win32_Process -Filter "ProcessId=$PID").ParentProcessId
            Wait-Process -Id $parent -ErrorAction SilentlyContinue
            {docker} rm -f {Quote(id)} *> $null
            """);
        var main = Encode($"""
            Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{watcher}'
            {docker} start -ai {Quote(id)}
            """);
        return ["powershell", "-NoProfile", "-EncodedCommand", main];
    }

    static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    static string Encode(string script) => Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));

    static ProcessCommand InTerminal(List<string> docker, string title, TerminalApp terminal)
    {
        return terminal.Kind switch
        {
            TerminalKind.WindowsTerminal => new ProcessCommand(terminal.Path, ["-w", "new", "--title", title, "--", .. docker]),
            TerminalKind.WindowsConsole => new ProcessCommand(docker[0], docker[1..]),
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
