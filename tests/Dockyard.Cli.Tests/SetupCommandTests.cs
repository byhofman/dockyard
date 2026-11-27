using Dockyard.Hosts;
using Dockyard.Setup;

namespace Dockyard.Cli.Tests;

public sealed class SetupCommandTests
{
    static readonly IReadOnlyList<ComponentInfo> Catalog =
    [
        new("DockyardCli", "Dockyard CLI"),
        new("Kind", "kind", Flag: "kind"),
        new("Tray", "Dockyard tray", Flag: "tray"),
        new(Dockyard.Core.ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"),
    ];

    [Theory]
    [InlineData("setup", "--silent", "--typo")]
    [InlineData("component", "add", "kind", "--typo")]
    public void An_unknown_option_is_refused_and_nothing_is_installed(params string[] args)
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = false };

        Assert.Equal(2, h.Run(args));

        Assert.Empty(h.Host.Requests);
    }

    [Fact]
    public void Setup_without_a_terminal_or_display_and_without_silent_asks_for_silent()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = false };

        Assert.Equal(2, h.Run("setup"));

        Assert.Contains("add --silent", h.Error);
        Assert.DoesNotContain("install", string.Join(',', h.Host.Calls));
    }

    [Fact]
    public void Setup_on_a_desktop_without_a_terminal_and_without_silent_also_exits_2()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = true };

        Assert.Equal(2, h.Run("setup"));

        Assert.Contains("run dockyard setup in a terminal", h.Error);
        Assert.Empty(h.Host.Requests);
    }

    [Fact]
    public void Silent_setup_with_a_component_on_a_headless_new_install_leaves_out_the_tray_and_the_dashboard()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = false };

        Assert.Equal(0, h.Run("setup", "--silent", "--with", "kind"));

        var request = Assert.Single(h.Host.Requests);
        Assert.False(request.Tray);
        Assert.False(request.Dashboard);
    }

    [Fact]
    public void Setup_in_a_terminal_asks_which_optional_components_and_then_installs()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = true, HasDesktop = false };
        h.Keys.Press(ConsoleKey.Spacebar, ' ');
        h.Keys.Press(ConsoleKey.Enter, '\r');

        Assert.Equal(0, h.Run("setup"));

        Assert.Contains("kind", h.Output);
        var request = Assert.Single(h.Host.Requests);
        Assert.Contains("Kind", request.Without);
        Assert.Contains("Tray", request.Without);
        Assert.False(request.Tray);
        Assert.False(request.Dashboard);
        Assert.Empty(h.Launcher.Sections);
    }

    [Fact]
    public void Setup_on_a_desktop_with_the_dashboard_chosen_opens_it_at_tools_once_done()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = true, HasDesktop = true };
        h.Keys.Press(ConsoleKey.Enter, '\r');

        Assert.Equal(0, h.Run("setup"));

        Assert.True(Assert.Single(h.Host.Requests).Dashboard);
        Assert.Equal(["tools"], h.Launcher.Sections);
    }

    [Fact]
    public void Setup_in_a_terminal_starts_with_only_what_a_new_install_has_by_default()
    {
        using var h = new CliHarness([new("DockyardCli", "Dockyard CLI"), new("LinuxEngine", "Linux engine", Flag: "linux-engine", Default: false), new("Kind", "kind", Flag: "kind")]);
        h.Keys.Press(ConsoleKey.Enter, '\r');

        Assert.Equal(0, h.Run("setup"));

        var request = Assert.Single(h.Host.Requests);
        Assert.Contains("LinuxEngine", request.Without);
        Assert.DoesNotContain("Kind", request.Without);
    }

    [Fact]
    public void Setup_on_a_desktop_without_the_dashboard_opens_nothing()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = true, HasDesktop = true };
        h.Keys.Press(ConsoleKey.DownArrow);
        h.Keys.Press(ConsoleKey.DownArrow);
        h.Keys.Press(ConsoleKey.Spacebar, ' ');
        h.Keys.Press(ConsoleKey.Enter, '\r');

        Assert.Equal(0, h.Run("setup"));

        Assert.False(Assert.Single(h.Host.Requests).Dashboard);
        Assert.Empty(h.Launcher.Sections);
    }

    [Fact]
    public void Silent_setup_installs_what_the_options_say_without_asking_or_opening_anything()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = true };

        Assert.Equal(0, h.Run("setup", "--silent", "--without", "kind,tray", "--with", "dashboard", "--source", "dockyard", "--trust-https", "--no-start", "--image", "linux.tar"));

        var request = Assert.Single(h.Host.Requests);
        Assert.True(request.Silent);
        Assert.True(request.TrustHttps);
        Assert.Equal("linux.tar", request.Image);
        Assert.DoesNotContain("start", h.Host.Calls);
        Assert.Empty(h.Launcher.Sections);
    }

    [Theory]
    [InlineData("--source", "brew", "--source is dockyard or package-manager")]
    [InlineData("--assets", "/no/such/folder", "--assets needs a folder that exists")]
    public void Setup_options_the_library_parser_rejects_exit_2_with_its_message(string option, string value, string message)
    {
        using var h = new CliHarness(Catalog);

        Assert.Equal(2, h.Run("setup", "--silent", option, value));
        Assert.Contains(message, h.Error);
        Assert.Empty(h.Host.Requests);
    }

    [Fact]
    public void Setup_sends_what_the_library_parser_reads_from_the_same_options()
    {
        string[] options = ["--silent", "--with", "kind", "--without", "tray,dashboard", "--source", "package-manager", "--trust-https", "--kubectl-shell", "--no-start", "--image", "linux.tar"];
        var parsed = SetupArguments.Parse(options, out var problems);
        Assert.Empty(problems);
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = true };

        Assert.Equal(0, h.Run(["setup", .. options]));

        var request = Assert.Single(h.Host.Requests);
        Assert.Equal(parsed.Silent, request.Silent);
        Assert.Equal(parsed.TrustHttps, request.TrustHttps);
        Assert.Equal(parsed.Image, request.Image);
        Assert.Equal(parsed.Without, ["tray", "dashboard"]);
        Assert.False(request.Tray);
        Assert.False(request.Dashboard);
        Assert.DoesNotContain("start", h.Host.Calls);
    }

    [Fact]
    public void Setup_options_that_name_nothing_exit_2()
    {
        using var h = new CliHarness(Catalog);

        Assert.Equal(2, h.Run("setup", "--silent", "--with", "kind", "--without", "kind"));
        Assert.Contains("kind is both wanted and not wanted", h.Error);
    }

    [Fact]
    public void The_resume_the_installer_schedules_finishes_the_setup_and_opens_the_dashboard()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = false };
        var assets = Directory.CreateTempSubdirectory("dockyard-assets-").FullName;
        try
        {
            Assert.Equal(0, h.Run("setup", "--resume", "--open-dashboard", "--assets", assets));

            Assert.Contains("resume", h.Host.Calls);
            Assert.Equal(["tools"], h.Launcher.Sections);
            Assert.Equal(assets, DockyardSetup.LocalAssets);
        }
        finally
        {
            DockyardSetup.LocalAssets = null;
            Directory.Delete(assets);
        }
    }

    [Fact]
    public void Component_ls_shows_the_catalog_rows_with_their_source()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;

        Assert.Equal(0, h.Run("component", "ls"));

        Assert.Contains(SettingDefinitions.PackageManager, Assert.Single(h.Output.Split('\n'), line => line.StartsWith("kind ")));
        Assert.Contains("Dockyard tray", h.Output);
        Assert.Matches("^FLAG +COMPONENT +INSTALLED +NEWEST +SOURCE +NOTE", h.Output);
    }

    [Fact]
    public void Component_ls_says_why_a_component_has_to_stay()
    {
        var row = new ComponentRow("LinuxEngine", "Docker Engine", "Docker", "29.9.2", "29.9.2", "Dockyard", "it runs your containers.", null, null, false, false, false);

        Assert.Equal("Required: it runs your containers.", ComponentListCommand.Note(row));
        Assert.Equal("Needs the Docker CLI.", ComponentListCommand.Note(row with { RequiredBecause = null, Needs = "Needs the Docker CLI." }));
    }

    [Fact]
    public void Component_add_and_remove_take_the_flag()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;

        Assert.Equal(0, h.Run("component", "remove", "kind", "--remove-data"));
        Assert.Equal(0, h.Run("component", "add", "kind"));
        Assert.Equal(2, h.Run("component", "add", "nothing"));

        Assert.Equal(["remove Kind", "add Kind"], h.Host.Calls);
        Assert.Contains("[1/1] Removing kind", h.Output);
    }

    [Fact]
    public void Update_takes_Dockyard_itself_along_last()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.Newest = new Version(2, 0, 0);
        h.Host.SelfComponent = "Tray";

        Assert.Equal(0, h.Run("update"));

        Assert.Equal("update DockyardCli,Kind,Dashboard,Tray", Assert.Single(h.Host.Calls, c => c.StartsWith("update")));
    }

    [Fact]
    public void Update_with_names_updates_only_those()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.Newest = new Version(2, 0, 0);

        Assert.Equal(0, h.Run("update", "tray", "DockyardCli"));

        Assert.Equal(["update Tray,DockyardCli"], h.Host.Calls);
    }

    [Fact]
    public void Update_fails_when_a_check_failed()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.LatestFailure = new HttpRequestException("github.com answered 503");

        Assert.Equal(1, h.Run("update"));
        Assert.Equal(1, h.Run("update", "--check"));

        Assert.Contains("github.com answered 503", h.Error);
    }

    [Fact]
    public void Any_failure_is_its_message_and_exit_1_without_a_stack_trace()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.FailureOnSetComponent = new HttpRequestException("github.com answered 503");

        Assert.Equal(1, h.Run("component", "add", "kind"));

        Assert.Contains("github.com answered 503", h.Error);
        Assert.DoesNotContain("   at ", h.Error);
    }

    [Fact]
    public void A_file_that_is_not_there_is_a_wrong_argument()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;

        Assert.Equal(2, h.Run("config", "apply", "--file", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json")));
        Assert.Contains("does not exist", h.Error);
    }

    [Fact]
    public void Update_check_only_lists()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.Newest = new Version(2, 0, 0);

        Assert.Equal(0, h.Run("update", "--check"));

        var lines = h.Output.ReplaceLineEndings("\n").Split('\n');
        Assert.Matches("^COMPONENT +INSTALLED +NEWEST +SOURCE +STATUS$", lines[0]);
        Assert.Matches("^kind +[0-9.]+ +2\\.0\\.0 +.+ +update available$", Assert.Single(lines, l => l.StartsWith("kind")));
        Assert.DoesNotContain(h.Host.Calls, c => c.StartsWith("update"));
    }

    [Fact]
    public void Uninstall_keeps_the_data_unless_asked()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;

        Assert.Equal(0, h.Run("uninstall", "--remove-data"));

        Assert.Equal(["uninstall data=True"], h.Host.Calls);
    }

    [Fact]
    public void Uninstall_asks_only_in_a_terminal_and_never_with_silent()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = true };
        h.Host.Installed = true;
        Assert.Equal(0, h.Run("uninstall"));
        Assert.False(h.Host.UninstalledSilently);

        h.Host.Installed = true;
        Assert.Equal(0, h.Run("uninstall", "--silent"));
        Assert.True(h.Host.UninstalledSilently);
    }
}
