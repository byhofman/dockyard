using Dockyard.Core;

namespace Dockyard.Setup.Tests;

public sealed class ComponentIndexTests
{
    [Fact]
    public void The_index_names_each_components_tag_version_and_what_it_needs()
    {
        var index = Updates.ParseIndex("""
            {
              "cli":   { "version": "0.9.0", "tag": "cli/0.9.0" },
              "dashboard": { "version": "0.9.2", "tag": "dashboard/0.9.2", "requires": { "cli": "0.9.0" } },
              "broken":    { "tag": "broken/x" },
              "comment":   "not a component"
            }
            """);

        Assert.Equal(["cli", "dashboard"], index.Keys);
        Assert.Equal(("dashboard/0.9.2", new Version(0, 9, 2)), (index["dashboard"].Tag, index["dashboard"].Version));
        Assert.Equal(new Version(0, 9, 0), index["dashboard"].Requires["cli"]);
        Assert.Empty(Updates.ParseIndex("{ not json"));
    }

    [Fact]
    public void A_component_tag_names_the_component_and_its_version()
    {
        Assert.Equal(new Version(0, 9, 0), Updates.ParseVersion("cli/0.9.0"));
        Assert.Equal("cli/1.2.3", Updates.ComponentTag(Updates.CliComponent, "1.2.3"));
    }
}
