using System.Net;
using System.Text.Json.Nodes;
using Dockyard.Agent;
using Xunit;

namespace Dockyard.Agent.Tests;

public class ServiceConfigTests
{
    static readonly DnsView Containers = new("containers", IPAddress.Parse("198.19.250.1"), 53, IPAddress.Parse("198.19.250.1"), [IPAddress.Parse("10.0.0.2")], Recursive: true);
    static readonly DnsView Host = new("host", IPAddress.Loopback, 2353, IPAddress.Parse("127.77.0.1"), [], Recursive: false);

    [Fact]
    public void Each_view_answers_the_zone_on_its_own_address_and_only_containers_get_upstream()
    {
        var corefile = ServiceConfig.Corefile([Containers, Host]);

        Assert.Contains("dockyard.internal:53 {\n    bind 198.19.250.1", corefile);
        Assert.Contains("dockyard.internal:2353 {\n    bind 127.0.0.1", corefile);
        Assert.Contains(".:53 {\n    bind 198.19.250.1", corefile);
        Assert.DoesNotContain(".:2353", corefile);
        Assert.Contains("forward . 10.0.0.2:53", corefile);
        Assert.Contains("file /etc/dockyard/host.zone", corefile);
    }

    [Fact]
    public void The_zone_has_each_container_and_a_wildcard_per_cluster()
    {
        var zone = ServiceConfig.Zone(IPAddress.Parse("127.77.0.1"), ["web.dockyard.internal", "db.dockyard.internal", "web.dockyard.internal"], ["dev"], 42);

        Assert.Contains("hostmaster.dockyard.internal. 42 ", zone);
        Assert.Contains("\ndb 5 IN A 127.77.0.1\nweb 5 IN A 127.77.0.1\n", zone);
        Assert.Contains("\n*.dev 5 IN A 127.77.0.1\n", zone);
    }

    [Fact]
    public void Without_upstream_servers_other_names_fail_instead_of_looping()
    {
        var corefile = ServiceConfig.Corefile([Containers with { Upstreams = [] }]);

        Assert.DoesNotContain("forward", corefile);
        Assert.Contains("rcode SERVFAIL", corefile);
    }

    [Fact]
    public void Hosts_lists_every_name_once()
    {
        Assert.Equal("198.19.250.1 a.dockyard.internal b.dockyard.internal\n",
            ServiceConfig.Hosts(IPAddress.Parse("198.19.250.1"), ["b.dockyard.internal", "a.dockyard.internal", "a.dockyard.internal"]));
        Assert.Equal("", ServiceConfig.Hosts(IPAddress.Loopback, []));
    }

    [Fact]
    public void Routes_clusters_and_the_fallback_go_to_traefik()
    {
        ProxyEntryPoint[] points = [new("web", "198.19.250.1:2380", false), new("websecure", "198.19.250.1:2381", true)];
        var json = JsonNode.Parse(ServiceConfig.TraefikDynamic(points,
            [new ProxyRoute("web.dockyard.internal", "http://172.17.0.2:80")],
            [new ClusterIngress("dev", IPAddress.Parse("172.18.0.5"), IPAddress.Parse("172.18.0.6"))],
            "http://198.19.250.1:2384", tls: true))!;

        var routers = json["http"]!["routers"]!;
        Assert.Equal("Host(`web.dockyard.internal`)", routers["c-web-dockyard-internal"]!["rule"]!.GetValue<string>());
        Assert.NotNull(routers["c-web-dockyard-internal-tls"]!["tls"]);
        Assert.Equal("http://172.17.0.2:80", json["http"]!["services"]!["c-web-dockyard-internal"]!["loadBalancer"]!["servers"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(@"HostRegexp(`^.+\.dev\.dockyard\.internal$`)", routers["k-dev"]!["rule"]!.GetValue<string>());
        Assert.Equal(1, routers["explain"]!["priority"]!.GetValue<int>());
        var tcp = json["tcp"]!["routers"]!["k-dev"]!;
        Assert.True(tcp["tls"]!["passthrough"]!.GetValue<bool>());
        Assert.Equal("websecure", tcp["entryPoints"]![0]!.GetValue<string>());
        Assert.Equal("/etc/dockyard/tls/cert.pem", json["tls"]!["stores"]!["default"]!["defaultCertificate"]!["certFile"]!.GetValue<string>());
    }

    [Fact]
    public void Without_a_certificate_there_are_no_https_routes()
    {
        ProxyEntryPoint[] points = [new("web", "198.19.250.1:2380", false), new("websecure", "198.19.250.1:2381", true)];
        var json = JsonNode.Parse(ServiceConfig.TraefikDynamic(points, [new ProxyRoute("web.dockyard.internal", "http://172.17.0.2:80")], [], "http://198.19.250.1:2384", tls: false))!;

        Assert.Null(json["http"]!["routers"]!["c-web-dockyard-internal-tls"]);
        Assert.Null(json["tls"]);
    }

    [Fact]
    public void Traefik_listens_where_each_engine_needs_it()
    {
        Assert.Contains(Services.EntryPoints(AgentMode.Wsl), e => e.Address == "127.0.0.1:2382");
        Assert.Contains(Services.EntryPoints(AgentMode.Host), e => e.Address == "127.0.0.1:2380");
        Assert.All(Services.EntryPoints(AgentMode.Vm), e => Assert.StartsWith("198.19.250.1:", e.Address));
        Assert.Contains("address: \"198.19.250.1:2380\"", ServiceConfig.TraefikStatic(Services.EntryPoints(AgentMode.Vm)));
    }
}
