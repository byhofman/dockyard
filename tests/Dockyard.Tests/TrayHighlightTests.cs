using Dockyard.Shared;

namespace Dockyard.Tests;

public class TrayHighlightTests
{
    [Theory]
    [InlineData("domains")]
    [InlineData("https")]
    [InlineData("proxy")]
    public void A_setting_to_highlight_goes_over_the_pipe_and_on_the_command_line(string setting)
    {
        var command = new TrayCommand(TrayCommandKind.Highlight, setting);

        Assert.Equal(command, TrayCommand.Parse(command.ToString()));
        Assert.Equal(command, TrayCommand.FromArgs(["--tray", "--highlight", setting]));
    }

    [Fact]
    public void Only_known_settings_are_highlighted()
    {
        Assert.Null(TrayCommand.Parse("highlight anything"));
        Assert.Equal(TrayCommandKind.Show, TrayCommand.FromArgs(["--tray", "--highlight", "anything"]).Kind);
    }
}
