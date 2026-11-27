using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class RunningAppTests
{
    [Theory]
    [InlineData("dockyard-cli", "Cli")]
    [InlineData("Dockyard.Dashboard", "Dashboard")]
    [InlineData("Dockyard.Tray", "Tray")]
    [InlineData("testhost", "Other")]
    [InlineData(null, "Other")]
    public void The_running_app_is_known_by_its_entry_assembly(string? assembly, string app) =>
        Assert.Equal(Enum.Parse<RunningApp>(app), ThisApp.Of(assembly));

    [Theory]
    [InlineData("Cli", false, "DockyardCli")]
    [InlineData("Cli", true, "DockyardCli")]
    [InlineData("Dashboard", false, ComponentRules.DashboardId)]
    [InlineData("Dashboard", true, ComponentRules.DashboardId)]
    [InlineData("Tray", true, ComponentRules.DashboardId)]
    [InlineData("Tray", false, "DockyardCli")]
    [InlineData("Other", false, "DockyardCli")]
    public void Dockyard_itself_is_the_running_app_and_for_the_tray_the_dashboard_when_there_is_one(string app, bool hasDashboard, string self) =>
        Assert.Equal(self, ThisApp.SelfComponent(Enum.Parse<RunningApp>(app), hasDashboard));

    [Theory]
    [InlineData("Cli", null)]
    [InlineData("Other", null)]
    [InlineData("Dashboard", ComponentRules.DashboardId)]
    [InlineData("Tray", ComponentRules.TrayId)]
    public void Only_the_tray_and_the_dashboard_hand_steps_over(string app, string? caller) =>
        Assert.Equal(caller, ThisApp.Caller(Enum.Parse<RunningApp>(app)));
}
