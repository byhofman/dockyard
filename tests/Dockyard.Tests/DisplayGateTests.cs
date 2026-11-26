namespace Dockyard.Tests;

public sealed class DisplayGateTests
{
    [Fact]
    public void The_display_check_reaches_the_system_libraries()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS");
        _ = Dockyard.Shared.DisplayGate.IsUsable();
    }
}
