using Dockyard.Core;

namespace Dockyard.Setup.Tests;

// The tray and the dashboard run Dockyard.Setup in their own process, with versions of their own.
[Collection(nameof(SharedHttp))]
public sealed class DockyardVersionTests
{
    static readonly Version Cli = new(1, 0, 0);
    const string TrayVersion = "2.0.0";

    [Fact]
    public async Task The_tray_and_the_dashboard_go_by_the_installed_cli_s_version_not_their_own()
    {
        using var settings = new TempSettings();
        _ = Facade.For(new FakeHost { Installed = true }, settings);
        var installed = await Updates.InstalledCli();
        Assert.Equal(Cli, installed);

        var version = Updates.DockyardVersion(installed, runsAsCli: false, TrayVersion);
        Assert.Equal(Cli, version);
        Assert.Equal("cli/1.0.0", Updates.ReleaseTag(version, tag: null));

        var needsNewerCli = new Updates.ComponentEntry("dashboard/2.0.0", new Version(2, 0, 0), new Dictionary<string, Version> { ["cli"] = new(2, 0, 0) });
        var needsThisCli = new Updates.ComponentEntry("dashboard/2.0.0", new Version(2, 0, 0), new Dictionary<string, Version> { ["cli"] = Cli });
        Assert.False(Updates.RequirementsMet(needsNewerCli, version));
        Assert.True(Updates.RequirementsMet(needsThisCli, version));

        Assert.False(Updates.BringsCliAlong(installed, runsAsCli: false, TrayVersion));
    }

    [Fact]
    public void The_cli_from_a_download_goes_by_its_own_version_only_until_one_is_installed()
    {
        Assert.Equal(new Version(1, 2, 3), Updates.DockyardVersion(null, runsAsCli: true, "1.2.3"));
        Assert.Equal(Cli, Updates.DockyardVersion(Cli, runsAsCli: true, "1.2.3"));
        Assert.True(Updates.BringsCliAlong(Cli, runsAsCli: true, "1.2.3"));
        Assert.False(Updates.BringsCliAlong(Cli, runsAsCli: true, "1.0.0"));
    }

    [Fact]
    public void Without_an_installed_cli_the_tray_names_no_release_and_the_index_decides()
    {
        Assert.Null(Updates.DockyardVersion(null, runsAsCli: false, TrayVersion));
        Assert.Null(Updates.ReleaseTag(null, tag: null));
    }
}
