using Avalonia;
using Dockyard.Dashboard.Features.Resources;
using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class WindowChromeTests
{
    [Fact]
    public void The_top_row_is_the_title_bar_on_macOS_and_Windows_and_Linux_keeps_the_system_s()
    {
        var mac = WindowChrome.For(HostOs.MacOS);
        Assert.True(mac.ExtendsIntoTitleBar);
        Assert.True(mac.RowInset.Left > 0, "the traffic lights sit on the left");
        Assert.Equal(0, mac.RowInset.Right);

        var windows = WindowChrome.For(HostOs.Windows);
        Assert.True(windows.ExtendsIntoTitleBar);
        Assert.Equal(0, windows.RowInset.Left);
        Assert.True(windows.RowInset.Right >= 3 * 46, "minimize, maximize and close sit on the right");

        var linux = WindowChrome.For(HostOs.Linux);
        Assert.False(linux.ExtendsIntoTitleBar);
        Assert.Equal(default(Thickness), linux.RowInset);
    }
}
