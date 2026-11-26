using Avalonia.Headless;
using Avalonia.Threading;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Shell;
using Dockyard.Engine;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

// What used to go to the tray goes through the facade now.
public sealed class FacadeWiringTests
{
    [Fact]
    public async Task Engines_start_and_switch_through_the_facade()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FacadeWiringTests).Assembly);
        await session.Dispatch(async () =>
        {
            var setup = new RecordingSetup();
            var shell = new ShellViewModel(setup);
            await shell.StartEngine("linux");
            var dialog = new Dialog("Switch", "", "Switch", _ => Task.CompletedTask);
            await Assert.ThrowsAsync<TimeoutException>(() => shell.Switch("win", dialog, TimeSpan.FromMilliseconds(50)));
            Assert.Equal(["start linux", "use win"], setup.Calls);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Whether_the_other_engine_stops_comes_from_the_facade_s_setting()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FacadeWiringTests).Assembly);
        await session.Dispatch(() =>
        {
            var setup = new RecordingSetup();
            setup.Set(SettingDefinitions.StopOtherEngineOnSwitch, SettingDefinitions.Off);
            var shell = new ShellViewModel(setup);
            shell.AskSwitch("win");
            Assert.Contains("The Linux engine keeps running", shell.Dialog!.Message);
            shell.Dialog.CancelCommand.Execute(null);

            setup.Set(SettingDefinitions.StopOtherEngineOnSwitch, SettingDefinitions.On);
            shell.AskSwitch("win");
            Assert.Contains("The Linux engine stops", shell.Dialog!.Message);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_cluster_s_certificate_authority_and_wants_come_from_the_facade()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FacadeWiringTests).Assembly);
        await session.Dispatch(async () =>
        {
            var setup = new RecordingSetup { CaPem = "chain" };
            var shell = new ShellViewModel(setup);
            Assert.Equal("chain", await shell.Clusters.ClusterCa("dev"));
            await shell.Clusters.PushWants();
            Assert.Equal(["ca dev", "wants"], setup.Calls);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_missing_tool_and_the_gear_lead_to_the_dashboard_s_own_sections()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FacadeWiringTests).Assembly);
        await session.Dispatch(() =>
        {
            var shell = new ShellViewModel(new RecordingSetup());
            shell.OpenToolsCommand.Execute(null);
            Assert.Equal("Tools", shell.CurrentSection.Title);
            shell.SettingsCommand.Execute(null);
            Assert.Equal("Settings", shell.CurrentSection.Title);
            Assert.DoesNotContain("Toolbox", ToolCheck.Missing(docker: false, compose: false, buildx: false));
            Assert.Contains("in Tools", ToolCheck.Missing(docker: true, compose: false, buildx: true));

            // kind is a missing tool too: its hint names Tools, and its link opens it.
            typeof(ClustersViewModel).GetField("kind", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(shell.Clusters, null);
            Assert.True(shell.Clusters.MissingKind);
            Assert.Equal("kind is not installed, so clusters cannot be made or shown. Add it in", shell.Clusters.Hint);
            Assert.Equal("Tools", shell.Clusters.HintLink);
            shell.ShowSection("Images");
            shell.Clusters.HintCommand.Execute(null);
            Assert.Equal("Tools", shell.CurrentSection.Title);
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Background_duties_report_warnings_to_the_activity_and_errors_as_errors()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FacadeWiringTests).Assembly);
        await session.Dispatch(() =>
        {
            var shell = new ShellViewModel(new RecordingSetup());
            shell.OnDuty(new SetupProgress("Looking for updates failed: offline", SetupProgressKind.Warning));
            shell.OnDuty(new SetupProgress("The certificate could not be renewed", SetupProgressKind.Error));
            Assert.Contains(shell.Activity.Lines, l => l.Text == "Looking for updates failed: offline");
            Assert.Equal(["The certificate could not be renewed"], shell.Toasts.Where(t => t.IsError).Select(t => t.Message));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Cluster_service_errors_are_said_once_and_not_under_each_cluster()
    {
        var report = new ClusterServicesStatus(
            [new ClusterStatus("dev", [], [], [], null), new ClusterStatus("test", [], [], [], "test's forwarder failed")],
            ["the cloud provider is not running"]);
        Assert.Equal("Load balancers for kind: the cloud provider is not running", LoadBalancers.ServiceErrors(report));
        Assert.Equal("", LoadBalancers.Notes(report.Clusters[0]));
        Assert.Equal("Load balancers for kind: test's forwarder failed", LoadBalancers.Notes(report.Clusters[1]));
        Assert.Equal("Load balancers for kind: the cloud provider is not running", LoadBalancers.ServiceErrors(new ClusterServicesStatus([], ["the cloud provider is not running"])));
    }
}
