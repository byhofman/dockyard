using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class SystemContainerTests
{
    static ContainerItem Item(string name, string state = "running", params (string Key, string Value)[] labels)
    {
        var item = new ContainerItem(name);
        item.Update(new ContainerSummary { Id = name, Names = ["/" + name], State = state, Labels = labels.ToDictionary(l => l.Key, l => l.Value) });
        return item;
    }

    [Fact]
    public void Dockyards_own_containers_are_one_group_at_the_end_and_not_managed_here()
    {
        var web = Item("web");
        var nfs = Item("dockyard-nfs", "exited", (SystemRow.ServiceLabel, "nfs"));
        var provider = Item("dockyard-cloud-provider-kind", "running", (SystemRow.HelperLabel, "cloud-provider-kind"));
        var zeta = Item("zeta");

        var rows = ContainerStore.BuildRows([nfs, zeta, provider, web], "");

        Assert.Equal<object>([web, zeta, new SystemRow(1, 2, true), provider, nfs], rows);
        Assert.True(nfs.IsSystem && nfs.IsGrouped && !nfs.IsManaged);
        Assert.True(web.IsManaged);
    }

    [Fact]
    public void The_group_can_be_collapsed_or_hidden_and_follows_the_filter()
    {
        var web = Item("web");
        var nfs = Item("dockyard-nfs", "running", (SystemRow.ServiceLabel, "nfs"));

        Assert.Equal<object>([web, new SystemRow(1, 1, false)], ContainerStore.BuildRows([web, nfs], "", collapsed: new HashSet<string> { SystemRow.Key }));
        Assert.Equal<object>([web], ContainerStore.BuildRows([web, nfs], "", showSystem: false));
        Assert.Equal<object>([new SystemRow(1, 1, true), nfs], ContainerStore.BuildRows([web, nfs], "nfs"));
    }
}
