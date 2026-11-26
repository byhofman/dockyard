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
    [InlineData("\x1b[0;32m  OK  \x1b[0m] Reached target \x1b[0;1;39mgraphical.target\x1b[0m - Graphical Interface.", "  OK  ] Reached target graphical.target - Graphical Interface.")]
    [InlineData("\x1b]0;my title\x07ready\x1b[2K\r", "ready")]
    [InlineData("plain\ttext", "plain\ttext")]
    public void Log_lines_lose_the_terminals_colours_and_control_characters(string line, string shown) =>
        Assert.Equal(shown, TerminalText.Plain(line));

    [Fact]
    public void Docker_cli_is_found_without_a_shell_path_after_a_mac_install()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Dockyard's own copy in ~/.dockyard/bin is the Mac's");
        var home = Directory.CreateTempSubdirectory("dockyard-cli").FullName;
        try
        {
            Assert.NotEqual(Path.Combine(home, ".dockyard", "bin", "docker"), DockerCli.Path(home));
            var cli = Path.Combine(home, ".dockyard", "bin", "docker");
            Directory.CreateDirectory(Path.GetDirectoryName(cli)!);
            File.WriteAllText(cli, "");
            Assert.Equal(cli, DockerCli.Path(home));

            var cmd = ExecCommand.Build("dockyard", "abc", "web", windowsContainer: false, TerminalApp.MacTerminal, cli);
            Assert.Contains(cli, cmd.Args[1]);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
