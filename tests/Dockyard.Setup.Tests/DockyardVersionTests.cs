using Dockyard.Core;

namespace Dockyard.Setup.Tests;

// Each app runs Dockyard.Setup in its own process: the version that counts is the installed copy of the app that runs.
[Collection(nameof(SharedHttp))]
public sealed class DockyardVersionTests
{
    static readonly Version Installed = new(1, 0, 0);
    const string OwnVersion = "2.0.0";

    [Theory]
    [InlineData("Cli")]
    [InlineData("Dashboard")]
    [InlineData("Tray")]
    public void Every_app_goes_by_the_installed_copy_not_its_own_version(string app)
    {
        var version = Updates.DockyardVersion(Installed, Enum.Parse<RunningApp>(app), OwnVersion);
        Assert.Equal(Installed, version);
        Assert.Equal("cli/1.0.0", Updates.ReleaseTag(version, tag: null));

        var needsNewer = new Updates.ComponentEntry("tray/2.0.0", new Version(2, 0, 0), new Dictionary<string, Version> { ["cli"] = new(2, 0, 0) });
        var needsThis = new Updates.ComponentEntry("tray/2.0.0", new Version(2, 0, 0), new Dictionary<string, Version> { ["cli"] = Installed });
        Assert.False(Updates.RequirementsMet(needsNewer, version));
        Assert.True(Updates.RequirementsMet(needsThis, version));
    }

    [Theory]
    [InlineData("Cli")]
    [InlineData("Dashboard")]
    public void An_app_from_a_download_goes_by_its_own_version_until_it_is_installed(string app) =>
        Assert.Equal(new Version(2, 0, 0), Updates.DockyardVersion(null, Enum.Parse<RunningApp>(app), OwnVersion));

    [Theory]
    [InlineData("Tray")]
    [InlineData("Other")]
    public void Without_an_installed_app_the_tray_names_no_release_and_the_index_decides(string app)
    {
        Assert.Null(Updates.DockyardVersion(null, Enum.Parse<RunningApp>(app), OwnVersion));
        Assert.Null(Updates.ReleaseTag(null, tag: null));
    }

    [Fact]
    public void Only_the_CLI_from_a_download_brings_its_own_package_along()
    {
        Assert.True(Updates.BringsCliAlong(Installed, RunningApp.Cli, "1.2.3"));
        Assert.False(Updates.BringsCliAlong(Installed, RunningApp.Cli, "1.0.0"));
        Assert.False(Updates.BringsCliAlong(Installed, RunningApp.Dashboard, "1.2.3"));
        Assert.False(Updates.BringsCliAlong(Installed, RunningApp.Tray, "1.2.3"));
    }

    [Fact]
    public async Task The_facade_points_the_version_at_the_installed_app_that_is_Dockyard_itself()
    {
        using var settings = new TempSettings();
        var host = new FakeHost { Installed = true, SelfComponent = ComponentRules.DashboardId };
        host.Missing.Add("DockyardCli");
        _ = Facade.For(host, settings);

        Assert.Equal(Installed, await Updates.InstalledApp());
        Assert.Null(await Updates.InstalledCli());
    }
}
