using System.Reflection;
using Avalonia.Headless;
using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Features.Setup;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

// Setup's checklist, Settings' groups and fresh values, and the clusters' hint and service errors.
public sealed class PolishTests
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    static Task Ui(Action test) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(PolishTests).Assembly).Dispatch(test, TestContext.Current.CancellationToken);

    static SetupStepState[] States(SetupRun run) => run.Checklist.Select(s => s.State).ToArray();

    [Fact]
    public void The_setup_checklist_lists_every_step_from_the_first_and_moves_down_it()
    {
        var run = new SetupRun("Installing Dockyard");
        run.Report(new SetupProgress("Installing Dockyard", Step: 1, Steps: 3) { Plan = ["Installing Dockyard", "Adding kind", "Adding Helm"] });
        Assert.Equal(["Installing Dockyard", "Adding kind", "Adding Helm"], run.Checklist.Select(s => s.Title));
        Assert.Equal([SetupStepState.Running, SetupStepState.ToDo, SetupStepState.ToDo], States(run));

        run.Report(new SetupProgress("brew install docker", SetupProgressKind.Command));
        run.Report(new SetupProgress("Adding kind", Step: 2, Steps: 3));
        run.Report(new SetupProgress("brew install kind", SetupProgressKind.Command));
        Assert.Equal([SetupStepState.Done, SetupStepState.Running, SetupStepState.ToDo], States(run));
        Assert.Equal(["brew install docker"], run.Checklist[0].Lines.Select(l => l.Line));
        Assert.Equal(["brew install kind"], run.Checklist[1].Lines.Select(l => l.Line));
        // The log is folded while it runs, and every line is still in it.
        Assert.False(run.ShowsDetails);
        Assert.False(run.Checklist.Any(s => s.IsExpanded));
        Assert.Equal(4, run.Lines.Count);

        run.Report(new SetupProgress("Adding Helm", Step: 3, Steps: 3));
        run.Finish();
        Assert.All(run.Checklist, s => Assert.Equal(SetupStepState.Done, s.State));
    }

    [Fact]
    public void A_failed_setup_opens_at_the_failing_step_with_its_lines()
    {
        var run = new SetupRun("Installing Dockyard");
        run.Report(new SetupProgress("Installing Dockyard", Step: 1, Steps: 3) { Plan = ["Installing Dockyard", "Adding kind", "Adding Helm"] });
        run.Report(new SetupProgress("Adding kind", Step: 2, Steps: 3));
        run.Report(new SetupProgress("brew install kind", SetupProgressKind.Command));
        run.Report(new SetupProgress("Permission denied", SetupProgressKind.Error));
        run.Finish("The setup did not finish.");

        Assert.Equal([SetupStepState.Done, SetupStepState.Failed, SetupStepState.ToDo], States(run));
        Assert.True(run.Checklist[1].IsExpanded);
        Assert.False(run.Checklist[0].IsExpanded);
        Assert.Equal(["brew install kind", "Permission denied"], run.Checklist[1].Lines.Select(l => l.Line));
        Assert.Equal("Permission denied", run.Error);
    }

    [Fact]
    public void A_run_without_a_plan_lists_its_steps_as_they_come_and_a_cancel_stops_the_running_one()
    {
        var run = new SetupRun("Installing Dockyard");
        run.Report(new SetupProgress("Installing Dockyard", Step: 1, Steps: 1));
        Assert.Equal([SetupStepState.Running], States(run));
        run.Finish("The setup was cancelled.");
        Assert.Equal([SetupStepState.Failed], States(run));
        Assert.True(run.Checklist[0].IsExpanded);
    }

    [Fact]
    public Task Settings_are_grouped_as_the_facade_groups_them() => Ui(() =>
    {
        var setup = new RecordingSetup();
        var settings = new SettingsViewModel(setup);
        var expected = setup.SettingDefinitions.Where(d => d.Key != SettingDefinitions.Source).GroupBy(d => d.Group).ToList();
        Assert.Equal(expected.Select(g => g.Key), settings.Groups.Select(g => g.Title));
        Assert.Equal(expected.Select(g => g.Select(d => d.Key)), settings.Groups.Select(g => g.Items.Select(i => i.Definition.Key)));
        Assert.Contains(settings.Groups, g => g.Title == SettingDefinitions.DomainsGroup);
    });

    [Fact]
    public Task Settings_is_no_sidebar_section_yet_opens_by_name_and_from_the_gear() => Ui(() =>
    {
        var shell = new ShellViewModel(new RecordingSetup());
        Assert.DoesNotContain(shell.Sections, s => s.Title == "Settings");

        shell.ShowSection("settings");
        Assert.Same(shell.Settings, shell.CurrentSection);

        shell.ShowSection("Tools");
        shell.SettingsCommand.Execute(null);
        Assert.Same(shell.Settings, shell.CurrentSection);
    });

    [Fact]
    public Task Settings_read_their_values_again_when_shown() => Ui(() =>
    {
        var setup = new RecordingSetup();
        var shell = new ShellViewModel(setup);
        var settings = (SettingsViewModel)shell.Settings.Content;
        var domains = settings.Items.Single(i => i.Definition.Key == SettingDefinitions.ContainerDomains);
        Assert.Equal(SettingDefinitions.Off, domains.Value);

        // Changed elsewhere meanwhile, as with the CLI or the tray.
        setup.Set(SettingDefinitions.ContainerDomains, SettingDefinitions.On);
        shell.ShowSection("Settings");
        Assert.Equal(SettingDefinitions.On, domains.Value);
        Assert.Empty(setup.Calls);
    });

    [Fact]
    public Task An_installed_Dockyard_opened_at_setup_can_close_it() => Ui(() =>
    {
        var shell = new ShellViewModel(new RecordingSetup());
        shell.ShowSection("setup");
        Assert.True(shell.Setup!.CanClose);
        shell.Setup.CloseCommand.Execute(null);
        Assert.False(shell.ShowsSetup);

        var fresh = new ShellViewModel(new RecordingSetup(installed: false));
        Assert.False(fresh.Setup!.CanClose);
    });

    static void UseEngine(ShellViewModel shell, string context, bool windows, bool connected)
    {
        var client = new DockerClient(Dockyard.Engine.Transport.DockerEndpoint.Parse(windows ? "npipe:////./pipe/docker_engine" : "unix:///nonexistent.sock"));
        if (windows) typeof(DockerClient).GetProperty(nameof(DockerClient.OsType))!.SetValue(client, "windows");
        typeof(EngineSession).GetField("client", Any)!.SetValue(shell.Session, client);
        typeof(EngineSession).GetProperty(nameof(EngineSession.ContextName))!.SetValue(shell.Session, context);
        typeof(EngineSession).GetProperty(nameof(EngineSession.State))!.SetValue(shell.Session, connected ? SessionState.Connected : SessionState.Unavailable);
    }

    [Fact]
    public Task On_the_Windows_engine_the_clusters_hint_says_kind_runs_on_Linux_whether_kind_is_there_or_not() => Ui(() =>
    {
        var shell = new ShellViewModel(new RecordingSetup());
        typeof(ClustersViewModel).GetField("kind", Any)!.SetValue(shell.Clusters, null);
        UseEngine(shell, "win", windows: true, connected: true);
        Assert.Equal("kind runs on the Linux engine. Switch to Linux to see and create clusters.", shell.Clusters.Hint);
        Assert.Null(shell.Clusters.HintLink);
        Assert.False(shell.Clusters.MissingKind);
    });

    [Fact]
    public Task A_cluster_service_error_goes_when_the_engine_is_gone_or_is_the_Windows_one() => Ui(() =>
    {
        {
            var shell = new ShellViewModel(new RecordingSetup());
            var error = typeof(ClustersViewModel).GetField("servicesError", Any)!;

            error.SetValue(shell.Clusters, "Load balancers for kind: the cloud provider is not running");
            UseEngine(shell, "win", windows: true, connected: true);
            Assert.True(shell.Clusters.Reload().IsCompleted);
            Assert.Equal("", shell.Clusters.ServicesError);
            Assert.False(shell.Clusters.HasServicesError);

            error.SetValue(shell.Clusters, "Load balancers for kind: the cloud provider is not running");
            UseEngine(shell, "linux", windows: false, connected: false);
            Assert.True(shell.Clusters.Reload().IsCompleted);
            Assert.Equal("", shell.Clusters.ServicesError);
        }
    });
}

public sealed class SetupRunWithoutPlanTests
{
    // A run that names only some steps lists those, in their order, and no row without a name.
    [Fact]
    public void Without_a_plan_only_named_steps_are_listed()
    {
        var run = new SetupRun("Updating");
        run.Report(new SetupProgress("Adding Helm", Step: 3, Steps: 4));
        Assert.Equal(["Adding Helm"], run.Checklist.Select(s => s.Title));
        run.Report(new SetupProgress("Setting up kubectl", Step: 4, Steps: 4));
        Assert.Equal(["Adding Helm", "Setting up kubectl"], run.Checklist.Select(s => s.Title));
        Assert.Equal([SetupStepState.Done, SetupStepState.Running], run.Checklist.Select(s => s.State));
        Assert.DoesNotContain(run.Checklist, s => s.Title.Length == 0);
    }
}
