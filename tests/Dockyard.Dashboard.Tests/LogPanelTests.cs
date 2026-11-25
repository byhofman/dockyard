using Dockyard.Dashboard.Shell;

namespace Dockyard.Dashboard.Tests;

public sealed class LogPanelTests
{
    [Fact]
    public void Side_by_side_shows_the_tabs_that_are_ticked()
    {
        var panel = new LogPanel(new ActivityLog());
        var activity = panel.Tabs[0];

        Assert.Equal([activity], panel.ShownTabs);
        activity.IsShown = false;
        Assert.Empty(panel.ShownTabs);
        activity.IsShown = true;
        Assert.Equal([activity], panel.ShownTabs);
        Assert.False(activity.CanClose);
        panel.Close(activity);
        Assert.Equal([activity], panel.Tabs);
    }
}
