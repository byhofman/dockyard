namespace Dockyard.Setup.Tests;

public class TrayLaunchTests
{
    [Theory]
    [InlineData("com.byhofman.dockyard", false)]
    [InlineData("application.com.byhofman.dockyard.78127796.78127807", true)]
    [InlineData(null, true)]
    public void Only_a_start_by_the_login_item_leaves_the_dashboard_closed(string? xpcServiceName, bool opensDashboard)
    {
        Assert.Equal(opensDashboard, TrayInstance.OpensDashboardAtStart(xpcServiceName));
    }
}
