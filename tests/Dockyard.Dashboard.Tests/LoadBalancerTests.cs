using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class LoadBalancerTests
{
    static ContainerItem Proxy(string id, string cluster, string service, params (int Host, int Private)[] ports)
    {
        var item = new ContainerItem(id);
        item.Update(new ContainerSummary
        {
            Id = id,
            Names = ["/kindccm-" + id],
            State = "running",
            Labels = new() { [LoadBalancers.ClusterLabel] = cluster, [LoadBalancers.NameLabel] = $"{cluster}/{service}" },
            Ports = ports.Select(p => new PortMapping { IP = "0.0.0.0", PublicPort = p.Host, PrivatePort = p.Private, Type = "tcp" }).ToList(),
            NetworkSettings = new() { Networks = new() { ["kind"] = new() { IPAddress = "172.18.0." + (id[0] - 'a' + 10) } } },
        });
        return item;
    }

    [Fact]
    public void A_clusters_load_balancers_are_its_proxies_with_their_ports_on_this_computer_without_envoys_admin_port()
    {
        var containers = new[]
        {
            Proxy("b", "dev", "shop/web", (44620, 80), (44621, 10000), (44622, 443)),
            Proxy("a", "dev", "default/api", (44630, 8080)),
            Proxy("c", "other", "default/x", (44640, 80)),
        };

        var rows = LoadBalancers.For("dev", containers);

        Assert.Equal(["default/api", "shop/web"], rows.Select(r => r.Service));
        Assert.Equal(["http://127.0.0.1:44620", "http://127.0.0.1:44622"], rows[1].Ports.Select(p => p.Url));
        Assert.Equal("80/tcp, 443/tcp", rows[1].PortsText);
        Assert.Equal(["172.18.0.10", "172.18.0.11"], rows.Select(r => r.Address));
    }

    [Fact]
    public void Proxies_show_under_their_cluster_and_not_in_the_container_list()
    {
        var web = new ContainerItem("w");
        web.Update(new ContainerSummary { Id = "w", Names = ["/web"], State = "running" });
        var rows = ContainerStore.BuildRows([web, Proxy("p", "dev", "default/api", (1, 80))], "");
        Assert.Equal([web], rows);
    }

    static ContainerItem Node(string cluster, string role, string state = "running")
    {
        var item = new ContainerItem($"{cluster}-{role}");
        item.Update(new ContainerSummary
        {
            Id = $"{cluster}-{role}", Names = [$"/{cluster}-{role}"], State = state, Image = "kindest/node:v1.37.0",
            Labels = new() { [KindClusters.ClusterLabel] = cluster, [KindClusters.RoleLabel] = role },
        });
        return item;
    }

    [Fact]
    public void A_cluster_is_one_row_with_its_nodes_and_then_its_load_balancers_and_collapses_like_a_project()
    {
        var web = new ContainerItem("w");
        web.Update(new ContainerSummary { Id = "w", Names = ["/web"], State = "running" });
        var proxy = Proxy("p", "dev", "default/api", (1, 80));
        var plane = Node("dev", "control-plane");
        var worker = Node("dev", "worker", state: "exited");
        List<ContainerItem> all = [web, proxy, worker, plane];

        var rows = ContainerStore.BuildRows(all, "");

        var cluster = Assert.IsType<ClusterRow>(rows[0]);
        Assert.Equal(("dev", "1/2 nodes running", "kind cluster · Kubernetes v1.37.0"), (cluster.Name, cluster.Summary, cluster.Location));
        Assert.Equal<object>([cluster, plane, worker, proxy, web], rows);
        var collapsed = ContainerStore.BuildRows(all, "", collapsed: new HashSet<string> { "kind:dev" });
        Assert.Equal<object>([cluster with { IsExpanded = false }, web], collapsed);
        Assert.Equal<object>([rows[0], plane, worker, proxy], ContainerStore.BuildRows(all, "dev"));
    }
}
