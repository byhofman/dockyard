using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

public sealed class ComponentRulesTests
{
    static readonly IReadOnlyList<ComponentInfo> Catalog =
    [
        new("DockyardCli", "Dockyard CLI", RequiredBecause: "it installs everything", RequiredFlag: "dockyard-cli"),
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
        Assert.Contains("it installs everything", ComponentRules.WhyNotRemovable(Catalog, "DockyardCli", new HashSet<string>()));
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
    public void The_Dockyard_CLI_is_required_and_the_tray_and_dashboard_can_go()
    {
#pragma warning disable CA1416 // Only the catalogs, which are plain data on any system.
        foreach (var catalog in new[] { WindowsHost.Catalog, MacHost.Catalog, LinuxHost.Catalog })
#pragma warning restore CA1416
        {
            var present = catalog.Select(c => c.Id).ToHashSet();
            Assert.Equal("DockyardCli", catalog[0].Id);
            Assert.NotNull(catalog[0].RequiredBecause);
            Assert.Contains("required", ComponentRules.Problems(catalog, [], ["dockyard-cli"], [])[0]);
            Assert.NotNull(ComponentRules.WhyNotRemovable(catalog, "DockyardCli", present));

            foreach (var id in new[] { "Tray", ComponentRules.DashboardId })
            {
                var component = catalog.Single(c => c.Id == id);
                Assert.True(component.IsOptional);
                Assert.Null(ComponentRules.WhyNotRemovable(catalog, id, present));
            }
            Assert.Empty(ComponentRules.Problems(catalog, [], ["tray", "dashboard"], []));
            Assert.Equal(["Tray"], ComponentRules.Removed(catalog, [], [], ["tray"]));
        }
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
    public void Only_a_Windows_client_has_a_desktop_to_start_the_tray_in(string? installationType, bool desktop) =>
        Assert.Equal(desktop, WindowsHost.IsClientSku(installationType));
}
