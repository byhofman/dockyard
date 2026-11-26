using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class StopProcessesTests
{
    [Fact]
    public async Task Stopping_an_app_by_its_path_never_stops_this_process()
    {
        var runner = new FakeRunner().Returns($"101\n{Environment.ProcessId}\n202\n");

        await runner.StopMatching("/dockyard-dashboard$");

        Assert.Equal(["pgrep -f /dockyard-dashboard$", "kill 101", "kill 202"], runner.Commands);
    }

    [Fact]
    public async Task Nothing_running_stops_nothing()
    {
        var runner = new FakeRunner().Returns("", exitCode: 1);

        await runner.StopMatching("/dockyard-tray$");

        Assert.Equal(["pgrep -f /dockyard-tray$"], runner.Commands);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class LinuxUpdateTests
{
    static readonly Release Cli = new(new Version(2, 0, 0), "cli/2.0.0", null);
    static readonly Release Dashboard = new(new Version(2, 1, 0), "dashboard/2.1.0", null);

    [Fact]
    public void The_dashboard_alone_comes_from_its_own_release_without_the_app()
    {
        var (app, dashboard) = LinuxHost.AppUpdate([(ComponentRules.DashboardId, Dashboard), ("Kind", null)]);
        Assert.Null(app);
        Assert.Equal(Dashboard, dashboard);
    }

    [Fact]
    public void With_the_command_the_app_update_brings_the_dashboard_along()
    {
        var (app, dashboard) = LinuxHost.AppUpdate([(ComponentRules.DashboardId, Dashboard), (nameof(Component.DockyardCli), Cli)]);
        Assert.Equal(Cli, app);
        Assert.Null(dashboard);
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WindowsCliSourceTests
{
    [Fact]
    public void Setup_run_from_the_dockyard_command_installs_that_very_file()
    {
        var assets = Directory.CreateTempSubdirectory("dockyard-assets-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(assets, "dockyard-windows-x64.exe"), "");
            Assert.Equal(@"C:\Users\me\Downloads\dockyard-windows-x64.exe", MachineApps.CliFile(@"C:\Users\me\Downloads\dockyard-windows-x64.exe", assets));
            Assert.Equal(Path.Combine(assets, "dockyard-windows-x64.exe"), MachineApps.CliFile(null, assets));
            Assert.Null(MachineApps.CliFile(null, null));
        }
        finally { Directory.Delete(assets, recursive: true); }
    }
}
