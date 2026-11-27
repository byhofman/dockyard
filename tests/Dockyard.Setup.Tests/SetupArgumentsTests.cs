using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class SetupArgumentsTests
{
    static SetupRequest Ok(params string[] args)
    {
        var request = SetupArguments.Parse(args, out var problems);
        Assert.Empty(problems);
        return request;
    }

    static IReadOnlyList<string> Problems(params string[] args)
    {
        SetupArguments.Parse(args, out var problems);
        return problems;
    }

    [Fact]
    public void No_arguments_is_an_interactive_request_with_the_defaults()
    {
        var request = Ok();

        Assert.Empty(request.With);
        Assert.Empty(request.Without);
        Assert.Null(request.Source);
        Assert.False(request.TrustHttps);
        Assert.False(request.KubectlShell);
        Assert.False(request.Silent);
        Assert.False(request.Resume);
        Assert.True(request.Start);
        Assert.Null(request.Image);
        Assert.False(request.RemoveData);
        Assert.False(request.OpenDashboard);
        Assert.Null(request.Assets);
    }

    [Fact]
    public void Silent_trust_https_kubectl_shell_remove_data_and_open_dashboard_are_switches()
    {
        var request = Ok("--silent", "--trust-https", "--kubectl-shell", "--remove-data", "--open-dashboard");

        Assert.True(request.Silent);
        Assert.True(request.TrustHttps);
        Assert.True(request.KubectlShell);
        Assert.True(request.RemoveData);
        Assert.True(request.OpenDashboard);
    }

    [Fact]
    public void No_start_turns_start_off()
    {
        Assert.False(Ok("--no-start").Start);
    }

    [Fact]
    public void Resume_is_silent_too()
    {
        var request = Ok("--resume");

        Assert.True(request.Resume);
        Assert.True(request.Silent);
    }

    [Fact]
    public void With_and_without_take_repeats_and_comma_lists()
    {
        var request = Ok("--with", "kind, helm", "--with", "tray", "--without", "kubectl,", "--without=dashboard");

        Assert.Equal(["kind", "helm", "tray"], request.With);
        Assert.Equal(["kubectl", "dashboard"], request.Without);
    }

    [Theory]
    [InlineData("dockyard", ToolSource.Dockyard)]
    [InlineData("package-manager", ToolSource.PackageManager)]
    public void Source_names_where_the_tools_come_from(string value, ToolSource expected)
    {
        Assert.Equal(expected, Ok("--source", value).Source);
        Assert.Equal(expected, Ok($"--source={value}").Source);
    }

    [Fact]
    public void An_unknown_source_is_a_problem()
    {
        Assert.Equal(["--source is dockyard or package-manager"], Problems("--source", "brew"));
    }

    [Fact]
    public void Image_is_taken_as_given()
    {
        Assert.Equal("engine.tar", Ok("--image", "engine.tar").Image);
    }

    [Fact]
    public void Assets_must_be_a_folder_that_exists_and_comes_back_in_full()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Equal(folder, Ok("--assets", folder).Assets);
            Assert.Equal(["--assets needs a folder that exists"], Problems("--assets", Path.Combine(folder, "missing")));
        }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public void Parsing_does_not_touch_the_global_assets_folder()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var before = DockyardSetup.LocalAssets;
            Ok("--assets", folder);
            Assert.Equal(before, DockyardSetup.LocalAssets);
        }
        finally { Directory.Delete(folder); }
    }

    [Theory]
    [InlineData("--with")]
    [InlineData("--without")]
    [InlineData("--source")]
    [InlineData("--image")]
    [InlineData("--assets")]
    public void An_option_that_needs_a_value_without_one_is_a_problem(string option)
    {
        Assert.Equal([$"{option} needs a value"], Problems(option));
        Assert.Equal([$"{option} needs a value"], Problems("--silent", option));
    }

    [Fact]
    public void A_switch_with_a_value_is_a_problem()
    {
        Assert.Equal(["--silent takes no value"], Problems("--silent=yes"));
    }

    [Fact]
    public void Unknown_options_and_stray_words_are_problems_and_all_are_reported()
    {
        Assert.Equal(["Unknown option --bogus", "Unexpected argument extra"], Problems("--bogus", "--silent", "extra"));
    }

    static AppCommand Command(params string[] args)
    {
        var command = SetupArguments.ParseCommand(args, out var problems);
        Assert.Empty(problems);
        return Assert.IsType<AppCommand>(command);
    }

    static IReadOnlyList<string> CommandProblems(params string[] args)
    {
        SetupArguments.ParseCommand(args, out var problems);
        return problems;
    }

    [Fact]
    public void Without_one_of_the_apps_actions_there_is_no_command()
    {
        Assert.Null(SetupArguments.ParseCommand([], out var none));
        Assert.Empty(none);
        Assert.Null(SetupArguments.ParseCommand(["--section", "setup"], out _));
    }

    [Fact]
    public void Setup_takes_setups_options_wherever_the_action_stands()
    {
        var command = Command("--silent", "--setup", "--with", "kind", "--no-start");

        Assert.Equal(AppAction.Setup, command.Action);
        Assert.True(command.Setup.Silent);
        Assert.Equal(["kind"], command.Setup.With);
        Assert.False(command.Setup.Start);
        Assert.Equal(["Unknown option --help"], CommandProblems("--setup", "--silent", "--help"));
    }

    [Fact]
    public void Update_names_components_or_none_for_everything_with_an_update()
    {
        Assert.Equal(AppAction.Update, Command("--update").Action);
        Assert.Empty(Command("--update").Components);
        Assert.Equal(["Tray", "DockyardCli", "Dashboard"], Command("--update", "Tray,DockyardCli", "Dashboard").Components);
        Assert.Equal(["Unknown option --remove-data"], CommandProblems("--update", "--remove-data"));
    }

    [Fact]
    public void Component_adds_or_removes_one_and_only_a_removal_takes_its_data()
    {
        var add = Command("--component", "add", "kind");
        Assert.Equal((AppAction.Add, "kind", false), (add.Action, Assert.Single(add.Components), add.RemoveData));
        var remove = Command("--component", "remove", "linux-engine", "--remove-data");
        Assert.Equal((AppAction.Remove, "linux-engine", true), (remove.Action, Assert.Single(remove.Components), remove.RemoveData));

        Assert.Equal(["--component is add or remove, then the component"], CommandProblems("--component"));
        Assert.Equal(["--component is add or remove, then the component"], CommandProblems("--component", "change", "kind"));
        Assert.Equal(["--component is add or remove, then the component"], CommandProblems("--component", "add"));
        Assert.Equal(["Unknown option --remove-data"], CommandProblems("--component", "add", "kind", "--remove-data"));
        Assert.Equal(["Unexpected argument helm"], CommandProblems("--component", "add", "kind", "helm"));
    }

    [Fact]
    public void Uninstall_keeps_the_data_unless_asked_and_takes_nothing_else()
    {
        Assert.Equal((AppAction.Uninstall, false), (Command("--uninstall").Action, Command("--uninstall").RemoveData));
        Assert.True(Command("--uninstall", "--remove-data").RemoveData);
        Assert.Equal(["Unknown option --with", "Unexpected argument kind"], CommandProblems("--uninstall", "--with", "kind"));
    }

    [Fact]
    public void Every_action_runs_silently_anyway_so_silent_is_allowed_with_each()
    {
        Assert.Equal(AppAction.Uninstall, Command("--uninstall", "--silent").Action);
        Assert.Equal(AppAction.Update, Command("--update", "--silent").Action);
        Assert.Equal(AppAction.Add, Command("--component", "add", "kind", "--silent").Action);
    }

    [Theory]
    [InlineData("Cli", "component remove tray --remove-data")]
    [InlineData("Dashboard", "--component remove tray --remove-data")]
    public void A_handed_over_step_is_written_as_the_app_that_finishes_it_reads_it(string app, string expected) =>
        Assert.Equal(expected, string.Join(' ', SetupArguments.ForApp(Enum.Parse<RunningApp>(app), ["component", "remove", "tray", "--remove-data"])));

    [Fact]
    public void What_is_handed_to_the_dashboard_reads_back_as_the_same_step()
    {
        var update = Command([.. SetupArguments.ForApp(RunningApp.Dashboard, ["update", "Dashboard", "DockyardCli"])]);
        Assert.Equal(AppAction.Update, update.Action);
        Assert.Equal(["Dashboard", "DockyardCli"], update.Components);
        var remove = Command([.. SetupArguments.ForApp(RunningApp.Dashboard, ["component", "remove", "tray", "--remove-data"])]);
        Assert.Equal((AppAction.Remove, "tray", true), (remove.Action, remove.Components.Single(), remove.RemoveData));
    }

    [Fact]
    public void Silent_is_asked_for_with_any_action()
    {
        Assert.True(Command("--uninstall", "--silent").Silent);
        Assert.False(Command("--uninstall").Silent);
        Assert.True(Command("--update", "--silent").Silent);
        Assert.True(Command("--setup", "--silent").Silent);
        Assert.False(Command("--setup").Silent);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    public void A_run_is_silent_when_asked_or_when_there_is_no_terminal_to_ask_in(bool asked, bool hasTerminal, bool silent) =>
        Assert.Equal(silent, SetupArguments.Silent(asked, hasTerminal));

    [Fact]
    public void The_dashboards_usage_names_every_action_and_every_setup_option()
    {
        var usage = SetupArguments.DashboardUsage;
        foreach (var part in new[] { "--section <name>", "--setup", "--update [component", "--component add|remove <component> [--remove-data]", "--uninstall [--remove-data]", "--help" })
            Assert.Contains(part, usage);
        foreach (var option in new[] { "--silent", "--trust-https", "--kubectl-shell", "--resume", "--no-start", "--open-dashboard", "--remove-data" })
            Assert.Contains(option, usage);
        foreach (var option in new[] { "--with <value>", "--without <value>", "--source <value>", "--image <value>", "--assets <value>" })
            Assert.Contains(option, usage);
    }
}
