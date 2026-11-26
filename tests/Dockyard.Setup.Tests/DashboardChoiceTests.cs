using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class DashboardChoiceTests
{
    [Theory]
    [InlineData(null, false, false, true)]   // new install: with the dashboard
    [InlineData(false, false, false, false)]
    [InlineData(null, true, false, false)]   // existing install keeps what it has, also the owner's skipped dashboard
    [InlineData(null, true, true, true)]
    [InlineData(true, true, false, true)]    // an explicit choice changes it
    [InlineData(false, true, true, false)]
    public void Dashboard_choice(bool? requested, bool installed, bool has, bool expected)
    {
        Assert.Equal(expected, DashboardChoice.Resolve(requested, installed, has));
    }

    [Fact]
    public void Source_takes_dockyard_or_package_manager()
    {
        Assert.Equal(ToolSource.Dockyard, ToolSourceArgument.Parse("dockyard"));
        Assert.Equal(ToolSource.PackageManager, ToolSourceArgument.Parse("package-manager"));
        Assert.Null(ToolSourceArgument.Parse("winget"));
        Assert.Null(ToolSourceArgument.Parse(null));
    }
}
