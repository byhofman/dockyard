namespace Dockyard.Engine.Tests;

// What the agent runs next to kind's clusters: the cloud provider, each cluster's forwarder and dockyard-nfs.
public sealed class KindServiceContainerTests
{
    static ClusterBalancer Balancer(string service, string container, params int[] ports) => new(service, container, ports);

    [Fact]
    public void The_cloud_provider_is_pinned_by_digest_hidden_as_dockyards_own_maps_ports_to_the_host_and_installs_nothing_else()
    {
        var spec = KindCloudProvider.Spec;
        Assert.Matches(@"@sha256:[0-9a-f]{64}$", spec.Image);
        Assert.Equal(KindCloudProvider.Name, spec.Name);
        Assert.Equal("cloud-provider-kind", spec.Labels["dockyard.service"]);
        Assert.Equal(["/var/run/docker.sock:/var/run/docker.sock"], spec.Binds);
        Assert.Equal(["--enable-lb-port-mapping", "--enable-default-ingress=false", "--gateway-channel=disabled"], spec.Command);
        Assert.Equal(KindCloudProvider.Setup, spec.Setup);
    }

    [Fact]
    public void A_clusters_ports_go_to_the_first_service_by_name_that_has_them_and_wait_for_one_that_does_not_exist_yet()
    {
        var (forwards, taken) = KindForwarder.Plan([80, 443, 8080],
            [Balancer("zz/other", "kindccm-c", 80), Balancer("default/api", "kindccm-a", 8081), Balancer("traefik/traefik", "kindccm-b", 80, 443)],
            new Dictionary<int, string>(), "dev");

        Assert.Equal([new PlannedForward(80, "kindccm-b", "traefik/traefik"), new PlannedForward(443, "kindccm-b", "traefik/traefik")], forwards);
        Assert.Empty(taken);
        Assert.Equal("80=kindccm-b,443=kindccm-b", KindForwarder.Describe(forwards));
    }

    [Fact]
    public void A_port_another_cluster_claimed_first_is_left_to_it()
    {
        var (forwards, taken) = KindForwarder.Plan([80, 443], [Balancer("traefik/traefik", "kindccm-b", 80, 443)],
            new Dictionary<int, string> { [80] = "aaa" }, "dev");

        Assert.Equal([443], forwards.Select(f => f.Port));
        Assert.Equal([new TakenPort(80, "aaa")], taken);
    }

    [Fact]
    public void The_forwarder_publishes_on_loopback_only_and_names_its_cluster()
    {
        var spec = KindForwarder.Spec("dev", [new(80, "kindccm-b", "traefik/traefik")]);
        var body = spec.CreateBody();

        Assert.Equal(KindForwarder.Prefix + "dev", spec.Name);
        Assert.Equal("dev", spec.Labels[KindForwarder.ClusterLabel]);
        Assert.Equal("127.0.0.1", body["HostConfig"]!["PortBindings"]!["80/tcp"]![0]!["HostIp"]!.GetValue<string>());
        Assert.Equal("80", body["HostConfig"]!["PortBindings"]!["80/tcp"]![0]!["HostPort"]!.GetValue<string>());
        Assert.Equal("kind", body["HostConfig"]!["NetworkMode"]!.GetValue<string>());
        Assert.Contains("socat TCP-LISTEN:80,fork,reuseaddr TCP:kindccm-b:80 &", spec.Command[^1]);
    }

    [Fact]
    public void The_nfs_server_is_privileged_on_kinds_network_and_serves_its_volume()
    {
        var spec = KindNfsServer.Spec;
        var body = spec.CreateBody();

        Assert.Equal("dockyard-nfs", spec.Name);
        Assert.Matches(@"@sha256:[0-9a-f]{64}$", spec.Image);
        Assert.True(body["HostConfig"]!["Privileged"]!.GetValue<bool>());
        Assert.Equal("dockyard-nfs:/export", body["HostConfig"]!["Binds"]![0]!.GetValue<string>());
        Assert.Equal("nfs", spec.Labels["dockyard.service"]);
        Assert.DoesNotContain("\r", spec.Command[^1]);
    }

    [Theory]
    [InlineData("Error response from daemon: driver failed programming external connectivity on endpoint dockyard-lb-dev: Bind for 127.0.0.1:80 failed: port is already allocated", 80)]
    [InlineData("Error starting userland proxy: listen tcp4 127.0.0.1:443: bind: address already in use", 443)]
    [InlineData("driver failed programming external connectivity on endpoint dockyard-lb-test (1a79): failed to bind host port 127.0.0.1:80/tcp: address already in use", 80)]
    [InlineData("Error response from daemon: No such image", null)]
    public void A_port_something_else_holds_is_found_in_dockers_error(string error, int? port) => Assert.Equal(port, KindForwarder.BusyPort(error));
}
