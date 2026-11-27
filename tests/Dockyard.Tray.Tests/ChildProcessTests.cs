using System.Diagnostics;

namespace Dockyard.Tray.Tests;

public class ChildProcessTests
{
    [Fact]
    public void A_program_the_tray_starts_does_not_get_the_trays_launchd_service_name()
    {
        var dashboard = new ProcessStartInfo("/Applications/Dockyard.app/Contents/MacOS/Dockyard Dashboard");
        dashboard.Environment["XPC_SERVICE_NAME"] = "com.byhofman.dockyard";

        var start = TrayController.ForChild(dashboard);

        Assert.False(start.Environment.ContainsKey("XPC_SERVICE_NAME"));
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), start.Environment["PATH"]);
    }
}
