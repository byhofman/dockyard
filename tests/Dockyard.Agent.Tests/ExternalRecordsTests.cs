using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Dockyard.Agent;
using Xunit;

namespace Dockyard.Agent.Tests;

public class ExternalRecordsTests
{
    static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    static string Response(params (string Key, string Value)[] kvs) => new JsonObject
    {
        ["kvs"] = new JsonArray(kvs.Select(kv => (JsonNode)new JsonObject { ["key"] = B64(kv.Key), ["value"] = B64(kv.Value) }).ToArray()),
    }.ToJsonString();

    [Fact]
    public void Records_come_from_the_coredns_layout_with_their_cluster()
    {
        var records = ExternalRecords.Parse(Response(
            ("/dockyard/dev/internal/dockyard/shop/1a2b3c4d", """{"host":"172.18.0.5","ttl":30}"""),
            ("/dockyard/qa/internal/dockyard/api/qa/9f8e7d6c", """{"host":"172.18.0.9"}""")));

        Assert.Equal(new ExternalRecord("dev", "shop.dockyard.internal", IPAddress.Parse("172.18.0.5")), records[0]);
        Assert.Equal(new ExternalRecord("qa", "qa.api.dockyard.internal", IPAddress.Parse("172.18.0.9")), records[1]);
    }

    [Fact]
    public void Names_outside_the_zone_and_hostnames_are_left_out()
    {
        var records = ExternalRecords.Parse(Response(
            ("/dockyard/dev/com/example/www/1a2b3c4d", """{"host":"172.18.0.5"}"""),
            ("/dockyard/dev/internal/dockyard/alias/1a2b3c4d", """{"host":"lb.example.com"}"""),
            ("/dockyard/dev/internal/dockyard/txt/1a2b3c4d", """{"text":"owner"}""")));

        Assert.Empty(records);
        Assert.Empty(ExternalRecords.Parse("{}"));
    }

    [Fact]
    public void The_range_covers_exactly_the_prefix()
    {
        var request = JsonNode.Parse(ExternalRecords.RangeRequest())!;
        Assert.Equal("/dockyard/", Encoding.UTF8.GetString(Convert.FromBase64String(request["key"]!.GetValue<string>())));
        Assert.Equal("/dockyard0", Encoding.UTF8.GetString(Convert.FromBase64String(request["range_end"]!.GetValue<string>())));
    }

    [Fact]
    public void Published_names_are_in_the_zone_unless_a_container_has_them()
    {
        var zone = ServiceConfig.Zone(IPAddress.Parse("198.19.250.1"), ["shop.dockyard.internal"], [], 1,
            [("shop.dockyard.internal", IPAddress.Parse("172.18.0.5")), ("api.dockyard.internal", IPAddress.Parse("172.18.0.6"))]);

        Assert.Contains("\nshop 5 IN A 198.19.250.1\n", zone);
        Assert.DoesNotContain("172.18.0.5", zone);
        Assert.Contains("\napi 5 IN A 172.18.0.6\n", zone);
    }

    [Fact]
    public void A_published_ingress_host_is_routed_to_its_cluster()
    {
        ProxyEntryPoint[] points = [new("web", "198.19.250.1:2380", false), new("websecure", "198.19.250.1:2381", true)];
        var ingress = new ClusterIngress("dev", IPAddress.Parse("172.18.0.5"), IPAddress.Parse("172.18.0.5"));
        var json = JsonNode.Parse(ServiceConfig.TraefikDynamic(points, [], [ingress], "http://198.19.250.1:2384", tls: false, [("shop.dockyard.internal", ingress)]))!;

        Assert.Equal("Host(`shop.dockyard.internal`)", json["http"]!["routers"]!["e-shop-dockyard-internal"]!["rule"]!.GetValue<string>());
        Assert.Equal("HostSNI(`shop.dockyard.internal`)", json["tcp"]!["routers"]!["e-shop-dockyard-internal"]!["rule"]!.GetValue<string>());
        Assert.Equal("172.18.0.5:443", json["tcp"]!["services"]!["e-shop-dockyard-internal"]!["loadBalancer"]!["servers"]![0]!["address"]!.GetValue<string>());
    }
}
