using Dockyard.Dashboard.Features.Resources;

namespace Dockyard.Dashboard.Tests;

public sealed class UsageFilterTests
{
    [Fact]
    public void It_shows_everything_what_is_used_or_what_is_not_with_their_counts()
    {
        var filter = new UsageFilter();
        var changed = 0;
        filter.Changed += () => changed++;
        filter.Count([true, false, false]);

        Assert.Equal(("All 3", "In use 1", "Unused 2"), (filter.AllText, filter.UsedText, filter.UnusedText));
        Assert.True(filter.Shows(true) && filter.Shows(false));

        filter.ShowCommand.Execute("Unused");
        Assert.Equal((false, true, true), (filter.Shows(true), filter.Shows(false), filter.IsUnused));

        filter.ShowCommand.Execute("Used");
        Assert.Equal((true, false), (filter.Shows(true), filter.Shows(false)));
        Assert.Equal(2, changed);
    }
}
