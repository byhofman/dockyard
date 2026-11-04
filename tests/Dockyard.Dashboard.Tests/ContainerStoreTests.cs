using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class ContainerStoreTests
{
    static ContainerSummary C(string id, string name, string state = "running", string? project = null, params int[] ports) => new()
    {
        Id = id,
        Names = ["/" + name],
        Image = "img-" + name,
        State = state,
        Status = state,
        Labels = project is null ? null : new() { [ContainerItem.ProjectLabel] = project, [ContainerItem.ServiceLabel] = name },
        Ports = ports.Select(p => new PortMapping { IP = "0.0.0.0", PrivatePort = 80, PublicPort = p, Type = "tcp" })
            .Concat(ports.Select(p => new PortMapping { IP = "::", PrivatePort = 80, PublicPort = p, Type = "tcp" })).ToList(),
    };

    static List<string> Describe(IEnumerable<object> rows) => rows.Select(r => r switch
    {
        GroupHeader h => $"#{h.Title} {h.Summary}",
        ContainerItem i => i.Name,
        _ => "?",
    }).ToList();

    [Fact]
    public void Groups_compose_projects_then_standalone()
    {
        var store = new ContainerStore();
        store.Reset([C("1", "redis"), C("2", "shop-db", project: "shop"), C("3", "shop-api", "exited", "shop"), C("4", "alpha-web", project: "alpha")]);

        Assert.Equal(["#alpha 1/1 running", "alpha-web", "#shop 1/2 running", "shop-api", "shop-db", "#Standalone 1/1 running", "redis"],
            Describe(store.Rows));
    }

    [Fact]
    public void Without_projects_there_are_no_headers()
    {
        var store = new ContainerStore();
        store.Reset([C("1", "b"), C("2", "a")]);
        Assert.Equal(["a", "b"], Describe(store.Rows));
    }

    [Fact]
    public void Filter_matches_name_image_and_project()
    {
        var store = new ContainerStore();
        store.Reset([C("1", "redis"), C("2", "db", project: "shop")]);

        store.Filter = "SHOP";
        Assert.Equal(["#shop 1/1 running", "db"], Describe(store.Rows));

        store.Filter = "img-red";
        Assert.Equal(["redis"], Describe(store.Rows));
    }

    [Fact]
    public void Upsert_keeps_item_instance_and_remove_drops_it()
    {
        var store = new ContainerStore();
        store.Reset([C("1", "web")]);
        var item = store.Get("1");

        store.Upsert(C("1", "web", "exited"));
        Assert.Same(item, store.Get("1"));
        Assert.False(item!.IsRunning);

        store.Upsert(C("2", "new"));
        Assert.Equal(2, store.Items.Count);

        store.Remove("1");
        Assert.Null(store.Get("1"));
        Assert.Equal(["new"], Describe(store.Rows));
    }

    [Fact]
    public void Ports_are_distinct_and_formatted()
    {
        var store = new ContainerStore();
        store.Reset([C("1", "web", ports: [8443, 8080])]);
        var item = store.Get("1")!;
        Assert.Equal("8080→80/tcp  8443→80/tcp", item.PortsText);
        Assert.Equal("http://127.0.0.1:8080", item.Ports[0].Url);
    }

    [Fact]
    public void Usage_history_keeps_last_60_samples_and_clears_when_stopped()
    {
        var store = new ContainerStore();
        store.Reset([C("1", "web")]);
        var item = store.Get("1")!;
        for (var i = 0; i < 70; i++) item.ApplyUsage(new ContainerUsage(i, (ulong)i * 10, 0));

        Assert.Equal(60, item.CpuHistory.Length);
        Assert.Equal(69, item.CpuHistory[^1]);
        Assert.Equal(10, item.CpuHistory[0]);

        store.Upsert(C("1", "web", "exited"));
        Assert.False(item.HasUsage);
        Assert.Empty(item.CpuHistory);
    }
}
