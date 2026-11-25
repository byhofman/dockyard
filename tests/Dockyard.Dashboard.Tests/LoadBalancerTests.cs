using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using Store = Dockyard.Dashboard.Store;

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

    static ContainerSummary Controller(string state, string? image) => new()
    {
        Id = "c",
        Names = ["/" + LoadBalancers.Name],
        State = state,
        Labels = image is null ? [] : new() { ["dockyard.cloud-provider-kind.setup"] = image },
    };

    [Fact]
    public void The_controller_runs_while_wanted_is_started_when_stopped_replaced_when_older_or_set_up_differently_and_removed_when_not_wanted()
    {
        Assert.Equal(LoadBalancers.Step.Run, LoadBalancers.Plan(true, null));
        Assert.Equal(LoadBalancers.Step.None, LoadBalancers.Plan(true, Controller("running", LoadBalancers.Setup)));
        Assert.Equal(LoadBalancers.Step.Start, LoadBalancers.Plan(true, Controller("exited", LoadBalancers.Setup)));
        Assert.Equal(LoadBalancers.Step.Replace, LoadBalancers.Plan(true, Controller("running", LoadBalancers.Image + " --enable-lb-port-mapping")));
        Assert.Equal(LoadBalancers.Step.Remove, LoadBalancers.Plan(false, Controller("running", LoadBalancers.Setup)));
        Assert.Equal(LoadBalancers.Step.None, LoadBalancers.Plan(false, null));
    }

    [Fact]
    public void The_controller_is_pinned_by_digest_hidden_as_dockyards_own_maps_ports_to_the_host_and_installs_nothing_else()
    {
        var args = LoadBalancers.RunArgs("linux");
        Assert.Matches(@"@sha256:[0-9a-f]{64}$", LoadBalancers.Image);
        Assert.Equal(["--context", "linux", "run", "-d", "--name", LoadBalancers.Name], args[..6]);
        Assert.Contains("dockyard.service=cloud-provider-kind", args);
        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", args);
        Assert.Equal([LoadBalancers.Image, "--enable-lb-port-mapping", "--enable-default-ingress=false", "--gateway-channel=disabled"], args[^4..]);
    }

    static LoadBalancer Balancer(string service, string container, params int[] ports) =>
        new(service, "172.18.0.9", ports.Select(p => new Store.PublishedPort(40000 + p, p, "tcp")).ToList(), container);

    [Fact]
    public void A_clusters_ports_go_to_the_first_service_by_name_that_has_them_and_wait_for_one_that_does_not_exist_yet()
    {
        var (forwards, taken) = LoadBalancers.PlanForwards([80, 443, 8080],
            [Balancer("default/api", "kindccm-a", 8081), Balancer("traefik/traefik", "kindccm-b", 80, 443), Balancer("zz/other", "kindccm-c", 80)],
            new Dictionary<int, string>(), "dev");

        Assert.Equal([new LoadBalancers.Forward(80, "kindccm-b", "traefik/traefik"), new LoadBalancers.Forward(443, "kindccm-b", "traefik/traefik")], forwards);
        Assert.Empty(taken);
        Assert.Equal("80=kindccm-b,443=kindccm-b", LoadBalancers.Describe(forwards));
    }

    [Fact]
    public void A_port_another_cluster_claimed_first_is_left_to_it()
    {
        var (forwards, taken) = LoadBalancers.PlanForwards([80, 443], [Balancer("traefik/traefik", "kindccm-b", 80, 443)],
            new Dictionary<int, string> { [80] = "aaa" }, "dev");

        Assert.Equal([443], forwards.Select(f => f.Port));
        Assert.Equal([(80, "aaa")], taken);
    }

    [Fact]
    public void The_forwarder_publishes_on_loopback_only_and_names_its_cluster()
    {
        var args = LoadBalancers.ForwarderArgs("linux", "dev", [new(80, "kindccm-b", "traefik/traefik")]);

        Assert.Contains("127.0.0.1:80:80", args);
        Assert.Contains($"{LoadBalancers.ForwarderClusterLabel}=dev", args);
        Assert.Equal(LoadBalancers.ForwarderPrefix + "dev", args[args.ToList().IndexOf("--name") + 1]);
        Assert.Contains("socat TCP-LISTEN:80,fork,reuseaddr TCP:kindccm-b:80 &", args[^1]);
    }

    [Theory]
    [InlineData("Error response from daemon: driver failed programming external connectivity on endpoint dockyard-lb-dev: Bind for 127.0.0.1:80 failed: port is already allocated", 80)]
    [InlineData("Error starting userland proxy: listen tcp4 127.0.0.1:443: bind: address already in use", 443)]
    [InlineData("driver failed programming external connectivity on endpoint dockyard-lb-test (1a79): failed to bind host port 127.0.0.1:80/tcp: address already in use", 80)]
    [InlineData("Error response from daemon: No such image", null)]
    public void A_port_something_else_holds_is_found_in_dockers_error(string error, int? port) => Assert.Equal(port, LoadBalancers.BusyPort(error));
}
