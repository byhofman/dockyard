using System.Runtime.InteropServices;
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

    const string Cli = "/x/dockyard";
    const string Dashboard = "/x/dockyard-dashboard";

    [Theory]
    [InlineData("Dashboard", false, "cli,dashboard", "Dashboard")]
    [InlineData("Dashboard", false, "cli", "Cli")]
    [InlineData("Dashboard", true, "cli,dashboard", "Cli")]
    [InlineData("Tray", false, "cli,dashboard", "Dashboard")]
    [InlineData("Tray", false, "cli", "Cli")]
    [InlineData("Other", false, "cli,dashboard", "Dashboard")]
    [InlineData("Cli", false, "cli,dashboard", "Cli")]
    [InlineData("Cli", false, "dashboard", "Dashboard")]
    public void A_step_goes_to_the_running_apps_own_copy_and_otherwise_the_dashboard_first_and_never_to_an_app_it_removes(string running, bool removesRunning, string installed, string expected)
    {
        var present = installed.Split(',').Select(a => a == "cli" ? Cli : Dashboard).ToHashSet();
        var (app, exe) = ThisApp.HandOffTarget(Enum.Parse<RunningApp>(running), removesRunning, present.Contains, Cli, Dashboard);
        Assert.Equal(Enum.Parse<RunningApp>(expected), app);
        Assert.Equal(app == RunningApp.Cli ? Cli : Dashboard, exe);
    }

    [Fact]
    public void Without_an_installed_app_nothing_can_finish_a_step()
    {
        Assert.Throws<InvalidOperationException>(() => ThisApp.HandOffTarget(RunningApp.Tray, false, _ => false, Cli, Dashboard));
        Assert.Throws<InvalidOperationException>(() => ThisApp.HandOffTarget(RunningApp.Cli, removesRunning: true, _ => true, Cli, dashboardExe: null));
    }

    [Fact]
    public void Desktop_detection_follows_the_platform()
    {
        Assert.False(Session.HasDesktop(OSPlatform.Linux, interactive: true, _ => null));
        Assert.True(Session.HasDesktop(OSPlatform.Linux, interactive: true, name => name == "WAYLAND_DISPLAY" ? "wayland-0" : null));
        Assert.True(Session.HasDesktop(OSPlatform.OSX, interactive: true, _ => null));
        Assert.False(Session.HasDesktop(OSPlatform.OSX, interactive: true, name => name == "SSH_CONNECTION" ? "1.2.3.4 22 5.6.7.8 22" : null));
    }
}
