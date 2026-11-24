using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class GlobalSearchTests
{
    sealed class Fake(params SearchResult[] results) : ISearchable
    {
        public object? Revealed { get; private set; }
        public void ApplySearch(string text) { }
        public IEnumerable<SearchResult> Find(string text) => results.Where(r => r.Title.Contains(text, StringComparison.OrdinalIgnoreCase));
        public void Reveal(object row) => Revealed = row;
    }

    static SearchResult R(string title, string? kind = null) => new(title, "", title, kind);

    [Fact]
    public void Matches_from_every_section_come_grouped_in_the_sidebars_order_with_a_few_of_each()
    {
        var sections = new[]
        {
            new Section("Containers", new Fake(R("web-1"), R("web-2"), R("web-3"), R("web-4"), R("web-5"), R("web-6"), R("web", "Compose projects")), null),
            new Section("Images", new Fake(R("nginx"), R("webapp:latest")), null),
            new Section("Disk", new object(), null),
        };

        var hits = SearchHit.From(" web ", sections);

        Assert.Equal(["Containers", "Containers", "Containers", "Containers", "Containers", "Compose projects", "Images"], hits.Select(h => h.Kind));
        Assert.Equal([true, false, false, false, false, true, true], hits.Select(h => h.StartsGroup));
        Assert.Equal("webapp:latest", hits[^1].Title);
        Assert.Empty(SearchHit.From("  ", sections));
    }

    [Fact]
    public void Opening_a_result_clears_the_search_shows_its_section_and_picks_it()
    {
        var shell = new ShellViewModel();
        var images = new Fake(R("nginx"));
        var section = new Section("Images", images, null);

        shell.Search = "ngi";
        Assert.True(shell.IsSearchOpen);
        shell.OpenHit(new SearchHit("Images", "nginx", "", section, "nginx", true));

        Assert.Same(section, shell.CurrentSection);
        Assert.Equal("", shell.Search);
        Assert.False(shell.IsSearchOpen);
        Assert.Equal("nginx", images.Revealed);
    }

    static ContainerSummary C(string id, string name, string? project = null) => new()
    {
        Id = id,
        Names = ["/" + name],
        Image = "img-" + name,
        State = "running",
        Status = "running",
        Labels = project is null ? null : new() { [ContainerItem.ProjectLabel] = project, [ContainerItem.ServiceLabel] = name },
    };

    [Fact]
    public void A_container_in_a_folded_project_is_found_and_unfolded_when_opened()
    {
        var shell = new ShellViewModel();
        var containers = shell.Containers;
        containers.Store.Reset([C("1", "redis"), C("2", "shop-db", "shop"), C("3", "shop-api", "shop")]);
        containers.Store.ToggleProject("shop");
        ISearchable searchable = containers;

        var found = searchable.Find("api").ToList();
        Assert.Equal(["shop-api"], found.Select(f => f.Title));

        searchable.Reveal(found[0].Row);
        Assert.Equal("shop-api", containers.Selected?.Name);
        Assert.Contains(containers.Store.Rows, r => r is ContainerItem { Name: "shop-api" });
    }
}
