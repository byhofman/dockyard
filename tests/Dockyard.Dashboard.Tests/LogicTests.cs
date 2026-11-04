using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Shared;

namespace Dockyard.Dashboard.Tests;

public sealed class LogicTests
{
    static DockerEvent E(string type, string action, string id = "c1") => new() { Type = type, Action = action, Actor = new EventActor { ID = id } };

    [Theory]
    [InlineData("container", "start", "Refresh")]
    [InlineData("container", "die", "Refresh")]
    [InlineData("container", "health_status: healthy", "Refresh")]
    [InlineData("container", "destroy", "Remove")]
    [InlineData("container", "exec_start: sh -c ls", "None")]
    [InlineData("container", "attach", "None")]
    [InlineData("image", "pull", "Images")]
    [InlineData("volume", "create", "Volumes")]
    [InlineData("network", "connect", "Networks")]
    [InlineData("daemon", "reload", "None")]
    public void Routes_events(string type, string action, string expectedKind)
    {
        var expected = Enum.Parse<RouteKind>(expectedKind);
        var route = EventRouter.Route(E(type, action));
        Assert.Equal(expected, route.Kind);
        if (expected is RouteKind.Refresh or RouteKind.Remove) Assert.Equal("c1", route.Id);
    }

    [Fact]
    public void Backoff_doubles_up_to_ten_seconds()
    {
        var seq = new List<double>();
        TimeSpan? d = null;
        for (var i = 0; i < 7; i++) seq.Add((d = Backoff.Next(d)).Value.TotalSeconds);
        Assert.Equal([0.5, 1, 2, 4, 8, 10, 10], seq);
    }

    [Fact]
    public void Exec_linux_container_without_windows_terminal()
    {
        var cmd = ExecCommand.Build("linux", "abc", "web", windowsContainer: false, TerminalApp.WindowsConsole);
        Assert.Equal("docker", cmd.File);
        Assert.Equal(["--context", "linux", "exec", "-it", "abc", "sh", "-c", "command -v bash >/dev/null && exec bash || exec sh"], cmd.Args);
    }

    [Fact]
    public void Exec_windows_container_in_windows_terminal()
    {
        var cmd = ExecCommand.Build("win", "abc", "iis", windowsContainer: true, new TerminalApp(TerminalKind.WindowsTerminal, @"C:\wt.exe"));
        Assert.Equal(@"C:\wt.exe", cmd.File);
        Assert.Equal(["-w", "new", "--title", "iis", "--", "docker", "--context", "win", "exec", "-it", "abc",
            "cmd", "/c", "where powershell >nul 2>&1 && powershell || cmd"], cmd.Args);
    }

    [Theory]
    [InlineData("/usr/bin/gnome-terminal", "--title|web|--|docker")]
    [InlineData("/usr/bin/konsole", "-e|docker")]
    [InlineData("/usr/bin/kitty", "--title|web|docker")]
    public void Exec_in_linux_terminals(string terminal, string prefix)
    {
        var cmd = ExecCommand.Build("default", "abc", "web", windowsContainer: false, new TerminalApp(TerminalKind.Linux, terminal));
        Assert.Equal(terminal, cmd.File);
        Assert.Equal(prefix.Split('|'), cmd.Args.Take(prefix.Split('|').Length));
        Assert.Equal("command -v bash >/dev/null && exec bash || exec sh", cmd.Args[^1]);
    }

    [Fact]
    public void Exec_in_mac_terminal_quotes_for_shell_and_applescript()
    {
        var cmd = ExecCommand.Build("dockyard", "abc", "web", windowsContainer: false, TerminalApp.MacTerminal);
        Assert.Equal("osascript", cmd.File);
        Assert.Equal("tell application \"Terminal\" to do script \"docker --context dockyard exec -it abc sh -c 'command -v bash >/dev/null && exec bash || exec sh'\"",
            cmd.Args[1]);
    }

    [Fact]
    public void Log_buffer_drops_oldest_at_capacity_and_filters()
    {
        var buffer = new LogBuffer(3);
        foreach (var t in new[] { "one", "Two", "three", "four" }) buffer.Add(new LogLine(LogStream.Stdout, null, t));

        Assert.Equal(["Two", "three", "four"], buffer.Lines.Select(l => l.Text));
        Assert.Equal(["Two"], buffer.Filtered("two").Select(l => l.Text));
        Assert.Equal(3, buffer.Filtered("").Count);
    }

    [Fact]
    public void Stats_selection_streams_all_up_to_limit_then_only_visible()
    {
        var visible = new HashSet<string> { "b", "x" };
        Assert.Equal(["a", "b"], StatsSelection.Choose(["a", "b"], visible, limit: 2).Order());
        Assert.Equal(["b"], StatsSelection.Choose(["a", "b", "c"], visible, limit: 2).Order());
    }

    [Theory]
    [InlineData("show", "Show", null)]
    [InlineData("settings", "Settings", null)]
    [InlineData("start linux", "Start", "linux")]
    [InlineData("switch win", "Switch", "win")]
    public void Parses_tray_commands(string line, string kindName, string? engine)
    {
        var kind = Enum.Parse<TrayCommandKind>(kindName);
        var cmd = TrayCommand.Parse(line);
        Assert.Equal(new TrayCommand(kind, engine), cmd);
        Assert.Equal(line, cmd!.Value.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("start")]
    [InlineData("start mac")]
    [InlineData("rm -rf /")]
    public void Rejects_invalid_tray_commands(string line) => Assert.Null(TrayCommand.Parse(line));

    [Fact]
    public void Tray_command_from_args()
    {
        Assert.Equal(new TrayCommand(TrayCommandKind.Settings), TrayCommand.FromArgs(["--tray", "--settings"]));
        Assert.Equal(new TrayCommand(TrayCommandKind.Start, "win"), TrayCommand.FromArgs(["--start", "win"]));
        Assert.Equal(new TrayCommand(TrayCommandKind.Show), TrayCommand.FromArgs(["--start", "bogus"]));
    }
}
