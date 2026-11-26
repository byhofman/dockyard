using System.Runtime.InteropServices;
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

    [Fact]
    public void Setup_without_a_terminal_or_display_and_without_silent_asks_for_silent()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = false, HasDesktop = false };

        Assert.Equal(2, h.Run("setup"));

        Assert.Contains("add --silent", h.Error);
        Assert.DoesNotContain("install", string.Join(',', h.Host.Calls));
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
    public void Setup_on_a_desktop_with_the_dashboard_chosen_opens_its_setup_screen()
    {
        using var h = new CliHarness(Catalog) { HasTerminal = true, HasDesktop = true };
        h.Keys.Press(ConsoleKey.Enter, '\r');

        Assert.Equal(0, h.Run("setup"));

        Assert.True(Assert.Single(h.Host.Requests).Dashboard);
        Assert.Equal(["setup"], h.Launcher.Sections);
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
            Assert.Equal(["setup"], h.Launcher.Sections);
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
    public void Update_leaves_Dockyard_itself_to_its_restart()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.Newest = new Version(2, 0, 0);
        h.Host.SelfComponent = "Tray";

        Assert.Equal(0, h.Run("update"));

        var update = Assert.Single(h.Host.Calls, c => c.StartsWith("update"));
        Assert.DoesNotContain("Tray", update);
        Assert.Contains("Kind", update);
    }

    [Fact]
    public void Update_check_only_lists()
    {
        using var h = new CliHarness(Catalog);
        h.Host.Installed = true;
        h.Host.Newest = new Version(2, 0, 0);

        Assert.Equal(0, h.Run("update", "--check"));

        Assert.Contains("2.0.0", h.Output);
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
    public void Desktop_detection_follows_the_platform()
    {
        Assert.False(Session.HasDesktop(OSPlatform.Linux, interactive: true, _ => null));
        Assert.True(Session.HasDesktop(OSPlatform.Linux, interactive: true, name => name == "WAYLAND_DISPLAY" ? "wayland-0" : null));
        Assert.True(Session.HasDesktop(OSPlatform.OSX, interactive: true, _ => null));
        Assert.False(Session.HasDesktop(OSPlatform.OSX, interactive: true, name => name == "SSH_CONNECTION" ? "1.2.3.4 22 5.6.7.8 22" : null));
    }
}
