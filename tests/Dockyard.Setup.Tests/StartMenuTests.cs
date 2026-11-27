using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public class StartMenuTests
{
    const string Dashboard = @"C:\Program Files\Dockyard\dockyard-dashboard.exe";
    const string Tray = @"C:\Program Files\Dockyard\dockyard-tray.exe";

    [Theory]
    [InlineData(true, true, Dashboard)]
    [InlineData(true, false, Dashboard)]
    [InlineData(false, true, Tray)]
    [InlineData(false, false, null)]
    public void The_entry_opens_the_dashboard_else_the_tray(bool dashboard, bool tray, string? expected)
    {
        Assert.Equal(expected, StartMenu.Target(dashboard, tray, Dashboard, Tray));
    }

    [Fact]
    public void The_script_writes_the_shortcut_with_every_path_quoted_for_PowerShell()
    {
        var script = StartMenu.Script(@"C:\Users\o'neil\Start Menu\Programs\Dockyard.lnk", Dashboard);

        Assert.Contains(@"CreateShortcut('C:\Users\o''neil\Start Menu\Programs\Dockyard.lnk')", script);
        Assert.Contains(@"$s.TargetPath = 'C:\Program Files\Dockyard\dockyard-dashboard.exe'", script);
        Assert.EndsWith("$s.Save()", script);
    }
}
