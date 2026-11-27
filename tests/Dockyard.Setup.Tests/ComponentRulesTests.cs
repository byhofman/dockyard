using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class ComponentRulesTests
{
    static readonly IReadOnlyList<ComponentInfo> Catalog =
    [
        new("DockyardCli", "Dockyard CLI", Flag: "cli"),
        new("DockerCli", "Docker CLI", RequiredBecause: "the rest uses it", RequiredFlag: "docker-cli"),
        new("Engine", "Windows engine", Flag: "windows-engine", IsEngine: true),
        new("LinuxEngine", "Linux engine", Flag: "linux-engine", IsEngine: true),
        new("Compose", "Compose", Flag: "compose"),
        new("Tray", "Dockyard tray", Flag: "tray"),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"),
    ];

    [Fact]
    public void What_is_required_is_refused_with_the_reason()
    {
        var why = ComponentRules.WhyNotRemovable(Catalog, "DockerCli", new HashSet<string> { "DockerCli", "Engine", "LinuxEngine" });
        Assert.Contains("the rest uses it", why);
    }

    [Fact]
    public void Either_app_can_go_while_the_other_stays_but_not_the_last_one()
    {
        Assert.Null(ComponentRules.WhyNotRemovable(Catalog, "DockyardCli", new HashSet<string> { "DockyardCli", ComponentRules.DashboardId }));
        Assert.Null(ComponentRules.WhyNotRemovable(Catalog, ComponentRules.DashboardId, new HashSet<string> { "DockyardCli", ComponentRules.DashboardId }));
        Assert.Equal(ComponentRules.LastAppReason, ComponentRules.WhyNotRemovable(Catalog, "DockyardCli", new HashSet<string> { "DockyardCli", "Tray" }));
        Assert.Equal(ComponentRules.LastAppReason, ComponentRules.WhyNotRemovable(Catalog, ComponentRules.DashboardId, new HashSet<string> { ComponentRules.DashboardId }));
    }

    [Fact]
    public void A_silent_request_keeps_at_least_one_app()
    {
        Assert.Empty(ComponentRules.Problems(Catalog, [], ["cli"], []));
        Assert.Empty(ComponentRules.Problems(Catalog, [], ["dashboard"], []));
        Assert.Equal([ComponentRules.LastAppReason], ComponentRules.Problems(Catalog, [], ["cli", "dashboard"], []));
        Assert.Equal([ComponentRules.LastAppReason], ComponentRules.Problems(Catalog, [], ["cli"], [ComponentRules.DashboardId]));
        Assert.Empty(ComponentRules.Problems(Catalog, ["dashboard"], ["cli"], [ComponentRules.DashboardId]));
    }

    [Fact]
    public void One_of_two_engines_can_go_but_not_the_last()
    {
        Assert.Null(ComponentRules.WhyNotRemovable(Catalog, "Engine", new HashSet<string> { "Engine", "LinuxEngine" }));
        Assert.Contains("last engine", ComponentRules.WhyNotRemovable(Catalog, "LinuxEngine", new HashSet<string> { "LinuxEngine" }));
    }

    [Fact]
    public void A_silent_request_is_refused_before_anything_changes()
    {
        Assert.Empty(ComponentRules.Problems(Catalog, ["compose"], ["windows-engine"], []));
        Assert.Contains("required", ComponentRules.Problems(Catalog, [], ["docker-cli"], [])[0]);
        Assert.Contains("does not exist", ComponentRules.Problems(Catalog, ["buildx"], [], [])[0]);
        Assert.Contains("engine", ComponentRules.Problems(Catalog, [], ["windows-engine", "linux-engine"], [])[0]);
        Assert.Contains("engine", ComponentRules.Problems(Catalog, [], ["linux-engine"], ["Engine"])[0]);
        Assert.Empty(ComponentRules.Problems(Catalog, ["windows-engine"], ["linux-engine"], ["Engine"]));
    }

    [Fact]
    public void What_was_removed_stays_removed_across_reruns_until_asked_for()
    {
        Assert.Equal(["Compose"], ComponentRules.Removed(Catalog, ["Compose"], [], []));
        Assert.Equal(["Compose", "Engine"], ComponentRules.Removed(Catalog, ["Compose"], [], ["windows-engine"]));
        Assert.Empty(ComponentRules.Removed(Catalog, ["Compose"], ["compose"], []));
    }

    [Fact]
    public void Neither_the_CLI_nor_the_dashboard_is_required_and_the_tray_can_go()
    {
#pragma warning disable CA1416 // Only the catalogs, which are plain data on any system.
        foreach (var catalog in new[] { WindowsHost.Catalog, MacHost.Catalog, LinuxHost.Catalog })
#pragma warning restore CA1416
        {
            var present = catalog.Select(c => c.Id).ToHashSet();
            var cli = catalog.Single(c => c.Id == "DockyardCli");
            Assert.Null(cli.RequiredBecause);
            Assert.Equal("cli", cli.Flag);
            Assert.True(cli.Default);

            foreach (var id in new[] { "DockyardCli", "Tray", ComponentRules.DashboardId })
            {
                Assert.True(catalog.Single(c => c.Id == id).IsOptional);
                Assert.Null(ComponentRules.WhyNotRemovable(catalog, id, present));
            }
            Assert.Empty(ComponentRules.Problems(catalog, [], ["cli"], []));
            Assert.Empty(ComponentRules.Problems(catalog, [], ["tray", "dashboard"], []));
            Assert.Equal([ComponentRules.LastAppReason], ComponentRules.Problems(catalog, [], ["cli", "dashboard"], []));
            Assert.Equal(["Tray"], ComponentRules.Removed(catalog, [], [], ["tray"]));
            Assert.Equal(["DockyardCli"], ComponentRules.Removed(catalog, [], [], ["cli"]));
        }
        Assert.Contains("cli", ComponentRules.KnownFlags);
    }

    [Fact]
    public void The_tray_and_the_dashboard_are_off_by_default_on_a_server_and_on_a_desktop()
    {
#pragma warning disable CA1416
        foreach (var (desktop, catalogs) in new[]
        {
            (true, new[] { WindowsHost.CatalogFor(desktop: true), LinuxHost.CatalogFor(desktop: true) }),
            (false, new[] { WindowsHost.CatalogFor(desktop: false), LinuxHost.CatalogFor(desktop: false) }),
        })
#pragma warning restore CA1416
            foreach (var catalog in catalogs)
            {
                Assert.All(catalog.Where(c => c.Id is "Tray" or ComponentRules.DashboardId), c => Assert.Equal(desktop, c.Default));
                Assert.All(catalog.Where(c => c.Id is not ("Tray" or ComponentRules.DashboardId)), c => Assert.True(c.Default));
            }
    }

    [Theory]
    [InlineData("Client", true)]
    [InlineData("Server", false)]
    [InlineData("Server Core", false)]
    [InlineData(null, false)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Only_a_Windows_client_gets_mirrored_networking(string? installationType, bool client) =>
        Assert.Equal(client, WindowsHost.IsClientSku(installationType));
}
