using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class ShellTerminalTests
{
    static Func<string, bool> On(params string[] present) => present.Contains;

    [Fact]
    public void macOS_opens_Terminal_with_AppleScript_running_the_command_quoted()
    {
        var command = ShellTerminal.For("it's \"dev\"", ShellTerminal.Os.MacOS, On());

        Assert.Equal("osascript", command.File);
        Assert.Contains("tell application \"Terminal\" to do script \"dockyard ctx 'it'\\\\''s \\\"dev\\\"'\"", command.Args);
    }

    [Fact]
    public void Windows_prefers_Windows_Terminal_and_falls_back_to_powershell()
    {
        var wt = ShellTerminal.For("dev", ShellTerminal.Os.Windows, On("wt"));
        var ps = ShellTerminal.For("it's", ShellTerminal.Os.Windows, On());

        Assert.Equal("wt", wt.File);
        Assert.Equal(["dockyard", "ctx", "dev"], wt.Args);
        Assert.Equal("powershell", ps.File);
        Assert.Equal(["-NoExit", "-Command", "dockyard ctx 'it''s'"], ps.Args);
    }

    [Fact]
    public void Linux_prefers_x_terminal_emulator_and_falls_back_to_gnome_terminal()
    {
        var x = ShellTerminal.For("dev", ShellTerminal.Os.Linux, On("x-terminal-emulator", "gnome-terminal"));
        var gnome = ShellTerminal.For("dev", ShellTerminal.Os.Linux, On("gnome-terminal"));

        Assert.Equal("x-terminal-emulator", x.File);
        Assert.Equal(["-e", "dockyard", "ctx", "dev"], x.Args);
        Assert.Equal("gnome-terminal", gnome.File);
        Assert.Equal(["--", "dockyard", "ctx", "dev"], gnome.Args);
    }

    [Fact]
    public void Any_dockyard_command_runs_the_same_way()
    {
        var mac = ShellTerminal.Running(["--help"], ShellTerminal.Os.MacOS, On());
        var ps = ShellTerminal.Running(["--help"], ShellTerminal.Os.Windows, On());
        var x = ShellTerminal.Running(["--help"], ShellTerminal.Os.Linux, On("x-terminal-emulator"));

        Assert.Contains("tell application \"Terminal\" to do script \"dockyard --help\"", mac.Args);
        Assert.Equal(["-NoExit", "-Command", "dockyard --help"], ps.Args);
        Assert.Equal(["-e", "dockyard", "--help"], x.Args);
    }

    [Fact]
    public void Linux_without_a_terminal_says_so()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ShellTerminal.For("dev", ShellTerminal.Os.Linux, On()));

        Assert.Contains("x-terminal-emulator", ex.Message);
    }
}
