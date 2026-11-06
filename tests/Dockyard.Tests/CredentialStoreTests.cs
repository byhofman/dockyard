using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class CredentialStoreTests
{
    static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Plain_logins_are_read_from_auth_and_identity_tokens()
    {
        var json = """{"auths":{"a.example":{"auth":"AUTH"},"b.example":{"identitytoken":"tok"},"c.example":{}}}""".Replace("AUTH", B64("me:pa:ss"));
        Assert.Equal([new PlainLogin("a.example", "me", "pa:ss"), new PlainLogin("b.example", "<token>", "tok")], CredentialStore.PlainLogins(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"auths":{}}""")]
    public void Nothing_to_move_means_no_logins(string? json) => Assert.Empty(CredentialStore.PlainLogins(json));

    [Fact]
    public void Adopting_a_store_removes_the_secrets_and_keeps_everything_else()
    {
        var json = """{"auths":{"a.example":{"auth":"AUTH"}},"currentContext":"orbstack","proxies":{"default":{"httpProxy":"x"}}}""".Replace("AUTH", B64("me:secret"));
        var adopted = CredentialStore.Adopted(json, "osxkeychain");
        var config = JsonNode.Parse(adopted)!;
        Assert.Equal("osxkeychain", config["credsStore"]!.GetValue<string>());
        Assert.Equal("orbstack", config["currentContext"]!.GetValue<string>());
        Assert.Equal("x", config["proxies"]!["default"]!["httpProxy"]!.GetValue<string>());
        Assert.NotNull(config["auths"]!["a.example"]);
        Assert.DoesNotContain(B64("me:secret"), adopted);
    }

    [Fact]
    public void Uninstall_only_takes_back_the_store_dockyard_set()
    {
        Assert.Null(CredentialStore.WithoutStore("""{"credsStore":"desktop"}""", "osxkeychain"));
        Assert.DoesNotContain("credsStore", CredentialStore.WithoutStore("""{"credsStore":"osxkeychain","currentContext":"x"}""", "osxkeychain")!);
    }

    [Fact]
    public void A_broken_config_is_refused_rather_than_overwritten() =>
        Assert.Throws<InvalidOperationException>(() => CredentialStore.HasStore("{not json"));

    [Fact]
    public void The_helper_is_told_what_docker_itself_would_tell_it()
    {
        var input = JsonNode.Parse(CredentialStore.HelperInput(new PlainLogin("a.example", "me", "s\"x")))!;
        Assert.Equal("a.example", input["ServerURL"]!.GetValue<string>());
        Assert.Equal("me", input["Username"]!.GetValue<string>());
        Assert.Equal("s\"x", input["Secret"]!.GetValue<string>());
    }
}

public sealed class LoopbackForwarderTests
{
    static bool HasIpv6Loopback()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetworkV6, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, 0));
            return true;
        }
        catch (System.Net.Sockets.SocketException) { return false; }
    }

    [Fact]
    public async Task A_connection_to_the_ipv6_loopback_reaches_the_ipv4_loopback_and_back()
    {
        Assert.SkipUnless(HasIpv6Loopback(), "No IPv6 loopback here");
        var ct = TestContext.Current.CancellationToken;
        using var service = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        service.Start();
        var port = ((System.Net.IPEndPoint)service.LocalEndpoint).Port;
        using var forwarder = new LoopbackForwarder(_ => { });
        forwarder.Apply([port]);

        using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
        await client.ConnectAsync(System.Net.IPAddress.IPv6Loopback, port, ct);
        using var accepted = await service.AcceptTcpClientAsync(ct);
        await client.GetStream().WriteAsync("ping"u8.ToArray(), ct);
        var buffer = new byte[4];
        await accepted.GetStream().ReadExactlyAsync(buffer, ct);
        Assert.Equal("ping"u8.ToArray(), buffer);
        await accepted.GetStream().WriteAsync("pong"u8.ToArray(), ct);
        await client.GetStream().ReadExactlyAsync(buffer, ct);
        Assert.Equal("pong"u8.ToArray(), buffer);
    }

    [Fact]
    public void A_port_someone_else_listens_on_stays_theirs_and_is_reported_once()
    {
        Assert.SkipUnless(HasIpv6Loopback(), "No IPv6 loopback here");
        using var other = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, 0);
        other.Start();
        var port = ((System.Net.IPEndPoint)other.LocalEndpoint).Port;
        List<string> messages = [];
        using var forwarder = new LoopbackForwarder(messages.Add);

        forwarder.Apply([port]);
        forwarder.Apply([port]);

        Assert.Empty(forwarder.Ports);
        Assert.Single(messages);
    }

    [Fact]
    public void Ports_that_are_no_longer_published_are_let_go()
    {
        Assert.SkipUnless(HasIpv6Loopback(), "No IPv6 loopback here");
        using var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, 0);
        free.Start();
        var port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        using var forwarder = new LoopbackForwarder(_ => { });

        forwarder.Apply([port]);
        Assert.Equal([port], forwarder.Ports);
        forwarder.Apply([]);
        Assert.Empty(forwarder.Ports);
    }
}
