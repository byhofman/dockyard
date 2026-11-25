using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using Dockyard.Agent;

namespace Dockyard.Agent.Tests;

public sealed class ClusterIngressTests
{
    [Theory]
    [InlineData("web.dev.dockyard.internal", "dev")]
    [InlineData("api.shop.dev.dockyard.internal", "dev")]
    [InlineData("web.dockyard.internal", null)]
    [InlineData("dockyard.internal", null)]
    [InlineData("web.dev.example.com", null)]
    [InlineData(".dev.dockyard.internal", null)]
    [InlineData("web.my.dev.dockyard.internal", "my.dev")]
    [InlineData("web.qa.dockyard.internal", null)]
    public void A_name_under_a_cluster_is_for_the_longest_cluster_name_it_ends_in(string name, string? cluster) =>
        Assert.Equal(cluster, DomainNames.ClusterOf(name, ["dev", "my.dev"]));

    [Fact]
    public void The_ingress_is_the_load_balancer_with_both_ports_whatever_the_order()
    {
        var other = ("dev", "a-other", IPAddress.Parse("172.18.0.9"), true, false);
        var traefik = ("dev", "z-traefik", IPAddress.Parse("172.18.0.4"), true, true);

        Assert.Equal(new ClusterIngress("dev", IPAddress.Parse("172.18.0.4"), IPAddress.Parse("172.18.0.4")), DomainTable.PickIngress("dev", [other, traefik]));
        Assert.Equal(DomainTable.PickIngress("dev", [traefik, other]), DomainTable.PickIngress("dev", [other, traefik]));
    }

    [Fact]
    public async Task The_server_name_comes_from_a_real_clients_hello_and_the_rest_is_replayed()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: true, (_, _, _, _) => true);
        _ = ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "Web.Dev.dockyard.internal" }, TestContext.Current.CancellationToken);

        var stream = accepted.GetStream();
        var header = new byte[TlsClientHello.RecordHeader];
        await stream.ReadExactlyAsync(header, TestContext.Current.CancellationToken);
        var length = TlsClientHello.RecordLength(header)!.Value;
        var record = new byte[length];
        header.CopyTo(record, 0);
        await stream.ReadExactlyAsync(record.AsMemory(TlsClientHello.RecordHeader), TestContext.Current.CancellationToken);

        Assert.Equal("web.dev.dockyard.internal", TlsClientHello.ServerName(record));
        Assert.Null(TlsClientHello.RecordLength("GET / HTTP/1.1"u8));
        Assert.Null(TlsClientHello.ServerName(new byte[12]));

        var replay = new ReplayStream(record, new MemoryStream([1, 2, 3]));
        var all = new byte[record.Length + 3];
        await replay.ReadExactlyAsync(all, TestContext.Current.CancellationToken);
        Assert.Equal([.. record, 1, 2, 3], all);
        ssl.Dispose();
    }
}
