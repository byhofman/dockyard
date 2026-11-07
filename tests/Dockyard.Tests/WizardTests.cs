using Dockyard.ViewModels;

namespace Dockyard.Tests;

public sealed class WizardTests
{
    [Fact]
    public async Task Wizard_on_an_install_without_the_dashboard_offers_to_add_it_and_then_to_remove_it()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, Dashboard = false };
        var vm = new MainViewModel(host, setupMode: true, userSettings: settings.Settings);
        await vm.LoadSetup();

        Assert.False(vm.ShowInstall);
        Assert.True(vm.ShowManage);
        Assert.True(vm.ShowWizardManage);
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
    public async Task Wizard_on_a_new_machine_asks_for_the_dashboard_and_honours_the_choice()
    {
        using var settings = new TempSettings();
        var host = new FakeHost();
        var vm = new MainViewModel(host, setupMode: true, userSettings: settings.Settings);
        await vm.LoadSetup();

        Assert.True(vm.ShowInstall);
        Assert.False(vm.ShowManage);
        Assert.True(vm.IncludeDashboard);

        vm.IncludeDashboard = false;
        await vm.InstallCommand.ExecuteAsync();

        Assert.False(host.Dashboard);
        Assert.True(vm.ShowManage);
        Assert.True(vm.Dashboard.CanAdd);
    }

    [Fact]
    public async Task Update_from_the_wizard_keeps_what_is_there()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, Dashboard = true };
        var vm = new MainViewModel(host, setupMode: true, userSettings: settings.Settings);
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
        var vm = new MainViewModel(host, setupMode: false, userSettings: settings.Settings);
        await vm.OnWindowShown();
        Assert.True(vm.ShowManage);
        Assert.False(vm.ShowWizardManage);
        Assert.True(vm.Dashboard.CanAdd);
    }
}
