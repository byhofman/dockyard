using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

// What the dashboard's Setup, Tools and Settings screens show, decided by the facade rather than by the screens.
public sealed class FacadePolishTests : IDisposable
{
    readonly TempSettings settings = new();
    readonly ProgressLines progress = new();

    public void Dispose() => settings.Dispose();

    DockyardSetup For(IEngineHost host) => Facade.For(host, settings);

    static readonly EngineInfo Linux = new("linux", "Linux", "Linux containers", "", "ctx", "unix:///x");
    static readonly EngineInfo Windows = new(EngineSettings.Windows, "Windows", "Windows containers", "", "win", "npipe:////./pipe/x");

    [Fact]
    public void Every_setting_belongs_to_a_group_and_the_groups_come_in_order()
    {
        var all = SettingDefinitions.For([Linux, Windows]);
        Assert.All(all, d => Assert.False(string.IsNullOrWhiteSpace(d.Group)));
        string GroupOf(string key) => all.First(d => d.Key == key).Group;
        Assert.Equal(SettingDefinitions.DomainsGroup, GroupOf(SettingDefinitions.ContainerDomains));
        Assert.Equal(SettingDefinitions.DomainsGroup, GroupOf(SettingDefinitions.TrustedHttps));
        Assert.Equal(SettingDefinitions.EngineGroup, GroupOf(SettingDefinitions.StartEngineAtLogon));
        Assert.Equal(SettingDefinitions.EngineGroup, GroupOf(SettingDefinitions.StopOtherEngineOnSwitch));
        Assert.Equal(SettingDefinitions.EngineGroup, GroupOf(SettingDefinitions.EngineFilePrefix + "linux"));
        Assert.Equal(SettingDefinitions.EngineGroup, GroupOf(SettingDefinitions.EngineFilePrefix + EngineSettings.Windows));
        Assert.Equal(SettingDefinitions.ShellGroup, GroupOf(SettingDefinitions.KubectlShell));
        // A group's settings sit together, so a front end can group them in the order they come.
        var groups = all.Select(d => d.Group).ToList();
        var order = groups.Distinct().ToList();
        Assert.Equal(order, groups.Where((g, i) => i == 0 || groups[i - 1] != g).ToList());
        Assert.Equal([SettingDefinitions.DomainsGroup, SettingDefinitions.EngineGroup, SettingDefinitions.ShellGroup], order.Where(g => g != SettingDefinitions.ToolsGroup));
    }

    // About two lines in the dashboard's settings column; the details are in the documentation.
    [Fact]
    public void Every_setting_says_what_it_does_in_one_or_two_lines()
    {
        foreach (var d in SettingDefinitions.For([Linux, Windows]))
            Assert.True(d.Description.Length <= 150, $"{d.Key}: {d.Description.Length} characters");
    }

    [Fact]
    public async Task Setup_names_every_step_with_the_first()
    {
        var host = new FakeHost
        {
            Installed = true,
            Components = [new("DockyardCli", "Dockyard CLI"), new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"), new("Compose", "Docker Compose", Flag: "compose"), new("Buildx", "Docker Buildx", Flag: "buildx")],
        };
        host.Missing.Add("Compose");
        settings.Settings.RemovedComponents = ["Compose"];

        await For(host).Setup(new SetupRequest(["compose"], ["buildx"], null, false, false, Silent: true, Resume: false) { Start = false }, progress, CancellationToken.None);

        var steps = progress.All.Where(p => p.Step is not null).ToList();
        Assert.Equal(["Installing Dockyard", "Adding Docker Compose", "Removing Docker Buildx"], steps[0].Plan);
        Assert.Equal(steps.Select(s => s.Line), steps[0].Plan);
        Assert.All(steps.Skip(1), s => Assert.Null(s.Plan));
    }

    [Fact]
    public void Dockyard_itself_cannot_be_removed_only_uninstalled()
    {
        var host = new FakeHost { Installed = true, Components = [new("DockyardCli", "Dockyard CLI", Flag: "cli"), new("Tray", "Dockyard tray", Flag: "tray")] };
        var rows = For(host).Components();
        Assert.False(rows.Single(r => r.Id == "DockyardCli").CanRemove);
        Assert.True(rows.Single(r => r.Id == "Tray").CanRemove);
    }

    [Fact]
    public void An_engine_has_data_to_remove_with_it_and_a_tool_has_none()
    {
        var host = new FakeHost
        {
            Installed = true,
            Components = [new("DockyardCli", "Dockyard CLI"), new("WindowsEngine", "Windows engine", Flag: "windows", IsEngine: true), new("Kind", "kind", Flag: "kind")],
        };
        var rows = For(host).Components();
        Assert.True(rows.Single(r => r.Id == "WindowsEngine").HasData);
        Assert.False(rows.Single(r => r.Id == "Kind").HasData);
    }

    [Fact]
    public void A_setup_request_comes_from_the_setting_values_a_front_end_shows()
    {
        var values = new Dictionary<string, string>
        {
            [SettingDefinitions.Source] = SettingDefinitions.DockyardSource,
            [SettingDefinitions.TrustedHttps] = SettingDefinitions.On,
            [SettingDefinitions.KubectlShell] = SettingDefinitions.Off,
        };
        var request = SetupRequest.FromSettings(["kind"], ["helm"], values);
        Assert.Equal(["kind"], request.With);
        Assert.Equal(["helm"], request.Without);
        Assert.Equal(ToolSource.Dockyard, request.Source);
        Assert.True(request.TrustHttps);
        Assert.False(request.KubectlShell);
        Assert.False(request.Silent);
        Assert.False(request.Resume);

        values[SettingDefinitions.Source] = "Homebrew";
        values[SettingDefinitions.KubectlShell] = SettingDefinitions.On;
        var other = SetupRequest.FromSettings([], [], values);
        Assert.Equal(ToolSource.PackageManager, other.Source);
        Assert.True(other.KubectlShell);
        Assert.Null(SetupRequest.FromSettings([], [], new Dictionary<string, string>()).Source);
    }
}
