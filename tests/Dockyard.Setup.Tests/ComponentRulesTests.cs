using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

public sealed class ComponentRulesTests
{
    static readonly IReadOnlyList<ComponentInfo> Catalog =
    [
        new("Cli", "Docker CLI", RequiredBecause: "the rest uses it", RequiredFlag: "cli"),
        new("Engine", "Windows engine", Flag: "windows-engine", IsEngine: true),
        new("LinuxEngine", "Linux engine", Flag: "linux-engine", IsEngine: true),
        new("Compose", "Compose", Flag: "compose"),
        new("App", "Dockyard", RequiredBecause: "it runs everything"),
        new(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard"),
    ];

    [Fact]
    public void What_is_required_is_refused_with_the_reason()
    {
        var why = ComponentRules.WhyNotRemovable(Catalog, "Cli", new HashSet<string> { "Cli", "Engine", "LinuxEngine" });
        Assert.Contains("the rest uses it", why);
        Assert.Contains("it runs everything", ComponentRules.WhyNotRemovable(Catalog, "App", new HashSet<string>()));
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
        Assert.Contains("required", ComponentRules.Problems(Catalog, [], ["cli"], [])[0]);
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
}
