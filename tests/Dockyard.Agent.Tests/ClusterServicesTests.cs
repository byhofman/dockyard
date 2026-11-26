using System.Net;
using Dockyard.Engine;

namespace Dockyard.Agent.Tests;

// The agent keeps kind's cloud provider, each cluster's forwarder and dockyard-nfs running from what the host wants,
// on an engine faked in memory.
public sealed class ClusterServicesTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dy-cs").FullName;
    readonly FakeEngine engine = new();

    public void Dispose() => Directory.Delete(dir, recursive: true);

    string WantsFile => Path.Combine(dir, "cluster-wants.json");
    ClusterServices Services() => new(engine, WantsFile, _ => { });
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_cloud_provider_runs_while_a_running_cluster_wants_a_load_balancer_and_stops_after()
    {
        engine.Node("dev");
        engine.Node("quiet");
        var services = Services();
        services.SetWants([new("dev", true, [], false), new("quiet", false, [], false)]);

        await services.Reconcile(Ct);
        Assert.Equal("running", engine.Find(KindCloudProvider.Name)?.State);
        Assert.Equal(KindCloudProvider.Setup, engine.Find(KindCloudProvider.Name)!.Labels![KindCloudProvider.SetupLabel]);

        engine.Find("dev-control-plane")!.State = "exited";
        await services.Reconcile(Ct);
        Assert.Null(engine.Find(KindCloudProvider.Name));

        engine.Find("dev-control-plane")!.State = "running";
        await services.Reconcile(Ct);
        Assert.NotNull(engine.Find(KindCloudProvider.Name));

        services.SetWants([new("dev", false, [], false), new("quiet", false, [], false)]);
        await services.Reconcile(Ct);
        Assert.Null(engine.Find(KindCloudProvider.Name));
    }

    [Fact]
    public async Task A_cloud_provider_set_up_differently_is_made_again_and_a_stopped_one_started()
    {
        engine.Node("dev");
        engine.Add(new ContainerSummary { Id = "old", Names = ["/" + KindCloudProvider.Name], State = "running", Labels = new() { [KindCloudProvider.SetupLabel] = "older" } });
        var services = Services();
        services.SetWants([new("dev", true, [], false)]);

        await services.Reconcile(Ct);
        Assert.NotEqual("old", engine.Find(KindCloudProvider.Name)!.Id);

        engine.Find(KindCloudProvider.Name)!.State = "exited";
        var id = engine.Find(KindCloudProvider.Name)!.Id;
        await services.Reconcile(Ct);
        Assert.Equal((id, "running"), (engine.Find(KindCloudProvider.Name)!.Id, engine.Find(KindCloudProvider.Name)!.State));
    }

    [Fact]
    public async Task Two_clusters_wanting_one_port_give_it_to_the_first_by_name()
    {
        engine.Node("aaa");
        engine.Node("dev");
        engine.Balancer("aaa", "traefik/traefik", "kindccm-a", 80);
        engine.Balancer("dev", "default/api", "kindccm-b", 8081);
        engine.Balancer("dev", "traefik/traefik", "kindccm-c", 80, 443, KindCloudProvider.EnvoyAdminPort);
        engine.Balancer("dev", "zz/other", "kindccm-d", 443);
        var services = Services();
        services.SetWants([new("dev", true, [80, 443, 8080], false), new("aaa", true, [80], false)]);

        await services.Reconcile(Ct);

        Assert.Equal("80=kindccm-a", engine.Find(KindForwarder.Prefix + "aaa")!.Labels![KindForwarder.ForwardsLabel]);
        Assert.Equal("443=kindccm-c", engine.Find(KindForwarder.Prefix + "dev")!.Labels![KindForwarder.ForwardsLabel]);
        Assert.Equal([443], engine.Specs[KindForwarder.Prefix + "dev"].LoopbackPorts);
        var dev = services.Status().Single(s => s.Cluster == "dev");
        Assert.Equal([new ClusterForward(443, "traefik/traefik")], dev.Forwards);
        Assert.Equal([new TakenPort(80, "aaa")], dev.Taken);
        Assert.Empty(dev.Busy);
    }

    [Fact]
    public async Task A_forwarder_goes_when_its_cluster_stops_or_is_gone_and_one_with_other_forwards_is_made_again()
    {
        engine.Node("dev");
        engine.Balancer("dev", "traefik/traefik", "kindccm-a", 80, 443);
        var services = Services();
        services.SetWants([new("dev", true, [80], false)]);
        await services.Reconcile(Ct);
        Assert.NotNull(engine.Find(KindForwarder.Prefix + "dev"));

        services.SetWants([new("dev", true, [80, 443], false)]);
        await services.Reconcile(Ct);
        Assert.Equal("80=kindccm-a,443=kindccm-a", engine.Find(KindForwarder.Prefix + "dev")!.Labels![KindForwarder.ForwardsLabel]);

        engine.Find("dev-control-plane")!.State = "exited";
        await services.Reconcile(Ct);
        Assert.Null(engine.Find(KindForwarder.Prefix + "dev"));

        engine.Find("dev-control-plane")!.State = "running";
        await services.Reconcile(Ct);
        engine.Remove("dev-control-plane");
        await services.Reconcile(Ct);
        Assert.Null(engine.Find(KindForwarder.Prefix + "dev"));
    }

    [Fact]
    public async Task A_port_another_program_holds_is_reported_and_tried_again_only_when_the_clusters_containers_change()
    {
        engine.Node("dev");
        engine.Balancer("dev", "traefik/traefik", "kindccm-a", 80, 443);
        engine.BusyOnHost.Add(80);
        var services = Services();
        services.SetWants([new("dev", true, [80, 443], false)]);

        await services.Reconcile(Ct);
        Assert.Equal("443=kindccm-a", engine.Find(KindForwarder.Prefix + "dev")!.Labels![KindForwarder.ForwardsLabel]);
        Assert.Equal([80], services.Status().Single().Busy);
        Assert.Equal([new ClusterForward(443, "traefik/traefik")], services.Status().Single().Forwards);

        engine.BusyOnHost.Clear();
        var runs = engine.Runs;
        await services.Reconcile(Ct);
        Assert.Equal(runs, engine.Runs);
        Assert.Equal([80], services.Status().Single().Busy);

        engine.Balancer("dev", "default/api", "kindccm-b", 8081);
        await services.Reconcile(Ct);
        Assert.Equal("80=kindccm-a,443=kindccm-a", engine.Find(KindForwarder.Prefix + "dev")!.Labels![KindForwarder.ForwardsLabel]);
        Assert.Empty(services.Status().Single().Busy);
    }

    [Fact]
    public async Task NFS_is_started_again_when_it_was_removed_while_a_cluster_uses_it()
    {
        engine.Node("dev");
        var services = Services();
        services.SetWants([new("dev", false, [], true)]);

        await services.Reconcile(Ct);
        Assert.Equal("running", engine.Find(KindNfsServer.ContainerName)?.State);
        Assert.True(engine.Specs[KindNfsServer.ContainerName].Privileged);

        engine.Remove(KindNfsServer.ContainerName);
        await services.Reconcile(Ct);
        Assert.Equal("running", engine.Find(KindNfsServer.ContainerName)?.State);

        engine.Find(KindNfsServer.ContainerName)!.State = "exited";
        engine.Find(KindNfsServer.ContainerName)!.NetworkSettings = new() { Networks = [] };
        await services.Reconcile(Ct);
        Assert.Equal("running", engine.Find(KindNfsServer.ContainerName)?.State);
        Assert.Contains(ServiceContainerSpec.Network, engine.Find(KindNfsServer.ContainerName)!.NetworkSettings!.Networks!.Keys);
    }

    [Fact]
    public async Task NFS_stays_while_a_stopped_cluster_stores_on_it_and_goes_once_no_cluster_does()
    {
        engine.Node("dev");
        var services = Services();
        services.SetWants([new("dev", false, [], true)]);
        await services.Reconcile(Ct);

        engine.Find("dev-control-plane")!.State = "exited";
        await services.Reconcile(Ct);
        Assert.NotNull(engine.Find(KindNfsServer.ContainerName));

        services.SetWants([]);
        await services.Reconcile(Ct);
        Assert.Null(engine.Find(KindNfsServer.ContainerName));
    }

    [Fact]
    public async Task Wants_survive_an_agent_restart()
    {
        engine.Node("dev");
        engine.Balancer("dev", "traefik/traefik", "kindccm-a", 8080);
        Services().SetWants([new("dev", true, [8080], true)]);

        var restarted = Services();
        await restarted.Reconcile(Ct);

        Assert.Equal("8080=kindccm-a", engine.Find(KindForwarder.Prefix + "dev")!.Labels![KindForwarder.ForwardsLabel]);
        Assert.NotNull(engine.Find(KindNfsServer.ContainerName));
        Assert.NotNull(engine.Find(KindCloudProvider.Name));
    }

    [Fact]
    public async Task A_cluster_the_host_has_no_wants_for_gets_the_cloud_provider_and_no_ports()
    {
        engine.Node("elsewhere");
        engine.Balancer("elsewhere", "traefik/traefik", "kindccm-a", 80);
        engine.Add(new ContainerSummary { Id = "nfs", Names = ["/" + KindNfsServer.ContainerName], State = "running", NetworkSettings = new() { Networks = new() { ["kind"] = new() } } });
        var services = Services();

        await services.Reconcile(Ct);

        Assert.NotNull(engine.Find(KindCloudProvider.Name));
        Assert.Null(engine.Find(KindForwarder.Prefix + "elsewhere"));
        Assert.NotNull(engine.Find(KindNfsServer.ContainerName));
    }

    [Fact]
    public async Task The_agent_api_takes_the_wants_and_answers_with_each_clusters_ports()
    {
        engine.Node("dev");
        engine.Balancer("dev", "traefik/traefik", "kindccm-a", 80);
        var services = Services();
        var table = new DomainTable(() => throw new InvalidOperationException("no engine here"), Path.Combine(dir, "domain-ports.json"), _ => { });
        var api = new AgentApi(table, new DomainProxy(table, _ => { }), clusters: services);

        Assert.Null(api.Reply("""{"op":"cluster-wants","wants":[{"cluster":"dev","loadBalancer":true,"ports":[80],"nfs":false}]}""").Error);
        await services.Reconcile(Ct);
        var reply = api.Reply("""{"op":"cluster-status"}""");

        var status = Assert.Single(reply.Clusters!);
        Assert.Equal(("dev", 80, "traefik/traefik"), (status.Cluster, status.Forwards.Single().Port, status.Forwards.Single().Service));
    }

    sealed class FakeEngine : IClusterEngine
    {
        readonly List<ContainerSummary> containers = [];
        public Dictionary<string, ServiceContainerSpec> Specs { get; } = [];
        public HashSet<int> BusyOnHost { get; } = [];
        public int Runs { get; private set; }
        int next;

        public ContainerSummary? Find(string name) => containers.FirstOrDefault(c => c.Names.Contains("/" + name));
        public void Add(ContainerSummary c) => containers.Add(c);
        public void Remove(string name) => containers.RemoveAll(c => c.Names.Contains("/" + name));

        public void Node(string cluster) => Add(new ContainerSummary
        {
            Id = cluster + "-control-plane", Names = [$"/{cluster}-control-plane"], State = "running",
            Labels = new() { ["io.x-k8s.kind.cluster"] = cluster, ["io.x-k8s.kind.role"] = "control-plane" },
        });

        public void Balancer(string cluster, string service, string container, params int[] ports) => Add(new ContainerSummary
        {
            Id = container, Names = ["/" + container], State = "running",
            Labels = new() { [KindCloudProvider.ClusterLabel] = cluster, [KindCloudProvider.NameLabel] = $"{cluster}/{service}" },
            Ports = ports.Select(p => new PortMapping { IP = "0.0.0.0", PrivatePort = p, PublicPort = 40000 + p, Type = "tcp" }).ToList(),
        });

        public Task<IReadOnlyList<ContainerSummary>> List(CancellationToken ct) => Task.FromResult<IReadOnlyList<ContainerSummary>>(containers.ToList());

        public Task Run(ServiceContainerSpec spec, CancellationToken ct)
        {
            Runs++;
            if (spec.LoopbackPorts.FirstOrDefault(BusyOnHost.Contains) is var busy and > 0)
                throw new DockerException(HttpStatusCode.InternalServerError, $"driver failed programming external connectivity on endpoint {spec.Name}: Bind for 127.0.0.1:{busy} failed: port is already allocated");
            Specs[spec.Name] = spec;
            Add(new ContainerSummary
            {
                Id = $"{spec.Name}-{++next}", Names = ["/" + spec.Name], State = "running", Labels = spec.Labels.ToDictionary(),
                NetworkSettings = new() { Networks = new() { [ServiceContainerSpec.Network] = new() } },
            });
            return Task.CompletedTask;
        }

        public Task Start(string id, CancellationToken ct)
        {
            containers.Single(c => c.Id == id).State = "running";
            return Task.CompletedTask;
        }

        public Task Remove(string id, CancellationToken ct)
        {
            containers.RemoveAll(c => c.Id == id);
            return Task.CompletedTask;
        }

        public Task Connect(string network, string id, CancellationToken ct)
        {
            var c = containers.Single(c => c.Id == id);
            c.NetworkSettings ??= new();
            c.NetworkSettings.Networks ??= [];
            c.NetworkSettings.Networks[network] = new();
            return Task.CompletedTask;
        }
    }
}
