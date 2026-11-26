using Dockyard.Core;
using Dockyard.ViewModels;

using Dockyard.Setup.Tests;

namespace Dockyard.Tests;

public sealed class WizardTests
{
    [Fact]
    public async Task The_window_on_an_install_without_the_dashboard_offers_to_add_it_and_then_to_remove_it()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, Dashboard = false };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: false);
        await vm.OnWindowShown();

        Assert.True(vm.ShowManage);
        Assert.Contains(vm.Dashboard, vm.Components);
        Assert.True(vm.Dashboard.CanAdd);
        Assert.False(vm.Dashboard.CanRemove);

        await vm.Dashboard.AddCommand!.ExecuteAsync();
        Assert.True(host.Dashboard);
        Assert.True(vm.Dashboard.CanRemove);
        Assert.False(vm.Dashboard.CanAdd);

        await vm.Dashboard.RemoveCommand!.ExecuteAsync();
        Assert.False(host.Dashboard);
        Assert.True(vm.Dashboard.CanAdd);
        Assert.Equal(["add", "remove"], host.Calls);
    }

    [Fact]
    public async Task Setup_on_an_older_install_offers_the_update_and_names_the_version()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true);
        await vm.LoadSetup();

        Assert.False(vm.ShowInstall);
        Assert.False(vm.ShowManage);
        Assert.True(vm.ShowWizardManage);
        Assert.Equal("Dockyard Setup", vm.WindowTitle);
        Assert.Equal(vm.InstalledVersion == Paths.AppVersion ? "Run the setup again" : $"Update to {Paths.AppVersion}", vm.ReinstallText);
    }

    [Fact]
    public async Task Setup_over_an_install_starts_from_what_it_has_and_adds_or_removes_what_is_changed()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, Dashboard = true, Components = [new("App", "Dockyard"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"), new("Compose", "Compose", Flag: "compose")] };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true, openInTray: () => Task.FromResult(true));
        await vm.LoadSetup();
        var compose = vm.OptionalChoices.Single(c => c.Id == "Compose");

        Assert.True(compose.Include);
        Assert.True(vm.IncludeDashboard);

        compose.Include = false;
        await vm.InstallCommand.ExecuteAsync();

        Assert.Contains("remove Compose", host.Calls);
        Assert.Contains("Compose", settings.Settings.RemovedComponents);
        Assert.True(host.Dashboard);

        host.Calls.Clear();
        var again = new MainViewModel(Facade.For(host, settings), setupMode: true, openInTray: () => Task.FromResult(true));
        await again.LoadSetup();
        compose = again.OptionalChoices.Single(c => c.Id == "Compose");
        Assert.False(compose.Include);

        compose.Include = true;
        again.IncludeDashboard = false;
        await again.InstallCommand.ExecuteAsync();

        Assert.Contains("add Compose", host.Calls);
        Assert.DoesNotContain("Compose", settings.Settings.RemovedComponents);
        Assert.False(host.Dashboard);
    }

    [Fact]
    public async Task Setup_on_a_new_machine_asks_for_the_dashboard_honours_the_choice_and_hands_over_to_the_installed_window()
    {
        using var settings = new TempSettings();
        var host = new FakeHost();
        var opened = 0;
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true, openInTray: () => { opened++; return Task.FromResult(true); });
        var handedOver = false;
        vm.HandedOver += () => handedOver = true;
        await vm.LoadSetup();

        Assert.True(vm.ShowInstall);
        Assert.False(vm.ShowManage);
        Assert.True(vm.IncludeDashboard);

        vm.IncludeDashboard = false;
        await vm.InstallCommand.ExecuteAsync();

        Assert.False(host.Dashboard);
        Assert.Equal(1, opened);
        Assert.True(handedOver);
    }

    [Fact]
    public async Task Setup_stays_open_when_the_installed_window_does_not_come_up()
    {
        using var settings = new TempSettings();
        var host = new FakeHost();
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true, openInTray: () => Task.FromResult(false));
        var handedOver = false;
        vm.HandedOver += () => handedOver = true;
        await vm.LoadSetup();
        await vm.InstallCommand.ExecuteAsync();

        Assert.False(handedOver);
        Assert.True(host.Installed);
    }

    [Fact]
    public async Task Update_from_the_wizard_keeps_what_is_there()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, Dashboard = true };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: true, openInTray: () => Task.FromResult(true));
        await vm.LoadSetup();
        await vm.InstallCommand.ExecuteAsync();
        Assert.True(host.Dashboard);
        Assert.Contains("install dashboard=True silent=False", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("uninstall", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tray_window_has_the_same_row()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true };
        var vm = new MainViewModel(Facade.For(host, settings), setupMode: false);
        await vm.OnWindowShown();
        Assert.True(vm.ShowManage);
        Assert.False(vm.ShowWizardManage);
        Assert.True(vm.Dashboard.CanAdd);
        Assert.Equal("Dockyard Toolbox", vm.WindowTitle);
    }

    [Theory]
    [InlineData("0.7.0", "0.7.0", true)]
    [InlineData("0.7.0", "0.7.1", true)]
    [InlineData("0.7.1", "0.7.0", false)]
    [InlineData("0.0.0", "0.0.0", true)]
    [InlineData("0.0.0", "0.7.0", false)]
    [InlineData("0.7.0", null, false)]
    public void Setup_hands_over_to_an_install_that_is_as_new_and_a_branch_build_only_to_itself(string mine, string? installed, bool handsOver) =>
        Assert.Equal(handsOver, TrayLink.HandsOver(Version.Parse(mine), installed is null ? null : Version.Parse(installed)));

    [Fact]
    public void The_tray_offers_the_update_with_its_version_and_Dockyard_first()
    {
        Assert.Null(UpdateOffers.Text([]));
        Assert.Equal("Update Dockyard to 0.7.1", UpdateOffers.Text([("App", "Dockyard", "0.7.1")]));
        Assert.Equal("Update Dockyard to 0.7.1 and 1 more", UpdateOffers.Text([("Kind", "kind", "0.34.0"), ("App", "Dockyard", "0.7.1")]));
        Assert.Equal("Update kind to 0.34.0", UpdateOffers.Text([("Kind", "kind", "0.34.0")]));
        Assert.Equal("Install 2 updates", UpdateOffers.Text([("Kind", "kind", "0.34.0"), ("Cli", "Docker CLI", "30.0.0")]));
    }

    [Fact]
    public void Release_notes_are_linked_for_Dockyards_own_parts()
    {
        var release = new Hosts.Release(new Version(0, 7, 1), "v0.7.1", null);
        Assert.Equal("https://github.com/byhofman/dockyard/releases/tag/v0.7.1", UpdateOffers.NotesUrl("App", release));
        Assert.NotNull(UpdateOffers.NotesUrl("Dashboard", release));
        Assert.Null(UpdateOffers.NotesUrl("Kind", release));
        Assert.Null(UpdateOffers.NotesUrl("App", null));
    }
}
