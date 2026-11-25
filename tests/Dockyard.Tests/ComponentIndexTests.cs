using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class ComponentIndexTests
{
    [Fact]
    public void The_index_names_each_components_tag_version_and_what_it_needs()
    {
        var index = Updates.ParseIndex("""
            {
              "toolbox":   { "version": "0.9.0", "tag": "toolbox/0.9.0" },
              "dashboard": { "version": "0.9.2", "tag": "dashboard/0.9.2", "requires": { "toolbox": "0.9.0" } },
              "broken":    { "tag": "broken/x" },
              "comment":   "not a component"
            }
            """);

        Assert.Equal(["toolbox", "dashboard"], index.Keys);
        Assert.Equal(("dashboard/0.9.2", new Version(0, 9, 2)), (index["dashboard"].Tag, index["dashboard"].Version));
        Assert.Equal(new Version(0, 9, 0), index["dashboard"].Requires["toolbox"]);
        Assert.Empty(Updates.ParseIndex("{ not json"));
    }

    [Fact]
    public void A_latest_release_with_a_component_tag_is_read_from_its_redirect()
    {
        Assert.Equal("toolbox/0.9.0", Updates.TagOfLatestRedirect(new Uri("https://github.com/byhofman/dockyard/releases/tag/toolbox%2F0.9.0")));
        Assert.Equal("toolbox/0.9.0", Updates.TagOfLatestRedirect(new Uri("https://github.com/byhofman/dockyard/releases/tag/toolbox/0.9.0")));
        Assert.Equal("v0.8.0", Updates.TagOfLatestRedirect(new Uri("https://github.com/byhofman/dockyard/releases/tag/v0.8.0")));
        Assert.Null(Updates.TagOfLatestRedirect(new Uri("https://github.com/byhofman/dockyard/releases")));
        Assert.Equal(new Version(0, 9, 0), Updates.ParseVersion("toolbox/0.9.0"));
        Assert.Equal("cli/1.2.3", Updates.ComponentTag(Updates.CliComponent, "1.2.3"));
    }
}
