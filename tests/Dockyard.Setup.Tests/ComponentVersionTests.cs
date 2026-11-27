using Dockyard.Core;

namespace Dockyard.Setup.Tests;

// Each component is released on its own: an asset comes from the release of the component that owns it, at the version of
// that component on this machine.
[Collection(nameof(SharedHttp))]
public sealed class ComponentVersionTests
{
    static readonly Version Installed = new(1, 0, 0);
    const string OwnVersion = "2.0.0";

    [Theory]
    [InlineData("Dashboard", "cli")]
    [InlineData("Tray", "cli")]
    [InlineData("Cli", "dashboard")]
    [InlineData("Cli", "tray")]
    [InlineData("Cli", "engine")]
    public void Another_component_goes_by_its_installed_version_not_the_running_app_s(string app, string component)
    {
        var version = Updates.VersionOf(component, Installed, Enum.Parse<RunningApp>(app), OwnVersion);
        Assert.Equal(Installed, version);
        Assert.Equal($"{component}/1.0.0", Updates.ReleaseTag(component, version, tag: null));
    }

    [Theory]
    [InlineData("Cli", "cli")]
    [InlineData("Dashboard", "dashboard")]
    public void The_running_app_puts_its_own_version_in_place(string app, string component)
    {
        Assert.Equal(new Version(2, 0, 0), Updates.VersionOf(component, Installed, Enum.Parse<RunningApp>(app), OwnVersion));
        Assert.Equal(new Version(2, 0, 0), Updates.VersionOf(component, null, Enum.Parse<RunningApp>(app), OwnVersion));
        Assert.Equal(Installed, Updates.VersionOf(component, Installed, Enum.Parse<RunningApp>(app), "0.0.0"));
    }

    [Fact]
    public void Without_a_version_or_with_a_development_build_the_index_decides_and_a_named_release_always_wins()
    {
        Assert.Null(Updates.ReleaseTag("tray", null, tag: null));
        Assert.Null(Updates.ReleaseTag("cli", new Version(0, 0, 0), tag: null));
        Assert.Equal("cli/3.0.0", Updates.ReleaseTag("cli", Installed, tag: "cli/3.0.0"));
    }

    [Fact]
    public void Every_requirement_is_checked_against_the_named_component_s_own_version()
    {
        var entry = new Updates.ComponentEntry("dashboard/2.0.0", new Version(2, 0, 0),
            new Dictionary<string, Version> { ["cli"] = new(2, 0, 0), ["tray"] = new(1, 5, 0) });
        Dictionary<string, Version?> versions = new() { ["cli"] = new(2, 0, 0), ["tray"] = new(1, 4, 0) };

        Assert.False(Updates.RequirementsMet(entry, c => versions.GetValueOrDefault(c)));
        versions["tray"] = new(1, 5, 0);
        Assert.True(Updates.RequirementsMet(entry, c => versions.GetValueOrDefault(c)));
        versions["cli"] = new(1, 9, 0);
        Assert.False(Updates.RequirementsMet(entry, c => versions.GetValueOrDefault(c)));
    }

    [Fact]
    public void A_component_that_is_not_there_or_a_development_build_meets_any_requirement()
    {
        var entry = new Updates.ComponentEntry("dashboard/2.0.0", new Version(2, 0, 0), new Dictionary<string, Version> { ["cli"] = new(2, 0, 0) });
        Assert.True(Updates.RequirementsMet(entry, _ => null));
        Assert.True(Updates.RequirementsMet(entry, _ => new Version(0, 0, 0)));
        Assert.True(Updates.RequirementsMet(new Updates.ComponentEntry("tray/1.0.0", Installed, new Dictionary<string, Version>()), _ => Installed));
    }

    [Theory]
    [InlineData("Cli", "cli")]
    [InlineData("Dashboard", "dashboard")]
    [InlineData("Tray", null)]
    [InlineData("Other", null)]
    public void Only_the_two_apps_are_components_of_their_own(string app, string? component) =>
        Assert.Equal(component, ThisApp.OwnComponent(Enum.Parse<RunningApp>(app)));

    [Fact]
    public async Task The_facade_points_each_component_s_version_at_the_host()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, SelfComponent = ComponentRules.DashboardId };
        host.Missing.Add("DockyardCli");
        _ = Facade.For(host, settings);

        Assert.Equal(Installed, await Updates.InstalledComponent(Updates.DashboardComponent));
        Assert.Equal(Installed, await Updates.InstalledComponent(Updates.TrayComponent));
        Assert.Null(await Updates.InstalledComponent(Updates.CliComponent));
    }
}
