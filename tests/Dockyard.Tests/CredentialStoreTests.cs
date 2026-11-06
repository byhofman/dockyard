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
    public void Adopting_a_store_keeps_an_entry_that_was_not_moved_and_an_empty_file_is_no_error()
    {
        var unreadable = "not base64!";
        var config = new JsonObject { ["auths"] = new JsonObject { ["a.example"] = new JsonObject { ["auth"] = B64("u:p") }, ["b.example"] = new JsonObject { ["auth"] = unreadable } } }.ToJsonString();
        var auths = JsonNode.Parse(CredentialStore.Adopted(config, "osxkeychain"))!["auths"]!;
        Assert.Null(auths["a.example"]!["auth"]);
        Assert.Equal(unreadable, auths["b.example"]!["auth"]!.GetValue<string>());

        Assert.False(CredentialStore.HasStore(""));
        Assert.Empty(CredentialStore.PlainLogins("  "));
    }

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

public sealed class EngineConfigTests
{
    [Fact]
    public void Dockyards_dns_goes_in_and_out_without_touching_other_settings()
    {
        var merged = EngineConfig.WithDockyard("""{"log-driver":"journald"}""");
        Assert.Contains("198.19.250.1", merged);
        Assert.Contains("journald", merged);

        var back = EngineConfig.WithoutDockyard(merged);
        Assert.DoesNotContain("198.19.250.1", back);
        Assert.Contains("journald", back);
    }

    [Fact]
    public void Dns_someone_configured_stays()
    {
        const string theirs = """{"dns":["10.0.0.2"]}""";
        Assert.True(EngineConfig.DnsIsSomeoneElses(theirs));
        Assert.Contains("10.0.0.2", EngineConfig.WithDockyard(theirs));
        Assert.DoesNotContain("198.19.250.1", EngineConfig.WithDockyard(theirs));
        Assert.Contains("10.0.0.2", EngineConfig.WithoutDockyard(theirs));
    }

    [Fact]
    public void A_broken_file_is_not_overwritten() =>
        Assert.Throws<InvalidOperationException>(() => EngineConfig.WithDockyard("{broken"));
}

public sealed class ProxyTests
{
    const string Scutil = """
        <dictionary> {
          ExceptionsList : <array> {
            0 : *.local
            1 : 169.254/16
          }
          FTPPassive : 1
          HTTPEnable : 1
          HTTPPort : 3128
          HTTPProxy : proxy.corp.example
          HTTPSEnable : 1
          HTTPSPort : 3129
          HTTPSProxy : 127.0.0.1
        }
        """;

    [Fact]
    public void The_macs_settings_are_read_from_scutil()
    {
        var proxy = HostProxySettings.FromScutil(Scutil.ReplaceLineEndings("\n"));
        Assert.NotNull(proxy);
        Assert.Equal("http://proxy.corp.example:3128", proxy.Http);
        Assert.Equal("http://127.0.0.1:3129", proxy.Https);
        Assert.Equal(["*.local", "169.254/16"], proxy.NoProxy);
    }

    [Theory]
    [InlineData("<dictionary> {\n  HTTPEnable : 0\n  HTTPProxy : x\n  HTTPPort : 1\n}")]
    [InlineData("<dictionary> {\n  ProxyAutoConfigEnable : 1\n  ProxyAutoConfigURLString : http://x/p.pac\n}")]
    [InlineData("<dictionary> {\n}")]
    public void No_fixed_proxy_means_none(string output) => Assert.Null(HostProxySettings.FromScutil(output));

    [Fact]
    public void Windows_has_one_proxy_for_all_or_one_per_protocol()
    {
        var one = HostProxySettings.FromWindows(1, "proxy:8080", "*.corp;<local>")!;
        Assert.Equal("http://proxy:8080", one.Http);
        Assert.Equal("http://proxy:8080", one.Https);
        Assert.Equal(["*.corp"], one.NoProxy);

        var two = HostProxySettings.FromWindows(1, "http=a:1;https=b:2;ftp=c:3", null)!;
        Assert.Equal("http://a:1", two.Http);
        Assert.Equal("http://b:2", two.Https);

        Assert.Null(HostProxySettings.FromWindows(0, "proxy:8080", null));
        Assert.Null(HostProxySettings.FromWindows(1, "", null));
    }

    [Fact]
    public void The_environment_wins_in_either_case()
    {
        var env = new Dictionary<string, string> { ["HTTPS_PROXY"] = "http://u:1", ["no_proxy"] = "a,b" };
        var proxy = HostProxySettings.FromEnvironment(n => env.GetValueOrDefault(n))!;
        Assert.Equal("http://u:1", proxy.Https);
        Assert.Null(proxy.Http);
        Assert.Equal(["a", "b"], proxy.NoProxy);
    }

    [Fact]
    public void A_proxy_on_the_hosts_loopback_is_reached_through_the_agent()
    {
        var engine = HostProxySettings.ForEngine(new ProxySettings("http://127.0.0.1:3128", "http://localhost:3129", ["*.corp"]));
        Assert.Equal("http://198.19.250.254:3128", engine.Http);
        Assert.Equal("http://198.19.250.254:3129", engine.Https);
        Assert.Contains("host.docker.internal", engine.NoProxy);
        Assert.Contains("*.corp", engine.NoProxy);
    }

    [Fact]
    public void The_engine_file_only_has_proxies_when_the_host_uses_one()
    {
        Assert.DoesNotContain("proxies", EngineConfig.Render(null));
        var rendered = EngineConfig.Render(new ProxySettings("http://p:1", null, []));
        Assert.Contains("\"http-proxy\": \"http://p:1\"", rendered);
        Assert.DoesNotContain("https-proxy", rendered);
    }

    [Fact]
    public void Client_proxies_only_apply_to_dockyards_engine_and_nothing_is_left_on_uninstall()
    {
        const string endpoint = "unix:///Users/a/.dockyard/docker.sock";
        const string theirs = """{"currentContext":"orbstack","proxies":{"default":{"httpProxy":"http://other:1"}}}""";
        var added = EngineConfig.WithClientProxy(theirs, endpoint, new ProxySettings("http://p:1", null, []))!;
        var config = System.Text.Json.Nodes.JsonNode.Parse(added)!;
        Assert.Equal("http://p:1", config["proxies"]![endpoint]!["httpProxy"]!.GetValue<string>());
        Assert.Equal("http://other:1", config["proxies"]!["default"]!["httpProxy"]!.GetValue<string>());

        Assert.Null(EngineConfig.WithClientProxy(added, endpoint, new ProxySettings("http://p:1", null, [])));

        var removed = EngineConfig.WithClientProxy(added, endpoint, null)!;
        Assert.DoesNotContain(endpoint, removed);
        Assert.Contains("http://other:1", removed);
        Assert.Null(EngineConfig.WithClientProxy(theirs, endpoint, null));
    }

    [Fact]
    public void On_a_linux_host_proxies_someone_set_stay()
    {
        const string theirs = """{"proxies":{"http-proxy":"http://theirs:1"}}""";
        Assert.Contains("http://theirs:1", EngineConfig.WithDockyard(theirs, new ProxySettings("http://p:1", null, [])));
        Assert.Contains("http://theirs:1", EngineConfig.WithDockyard(theirs));
        var ours = EngineConfig.WithDockyard("{}", new ProxySettings("http://p:1", null, []));
        Assert.Contains("http://p:1", ours);
        Assert.DoesNotContain("http://p:1", EngineConfig.WithDockyard(ours));
    }
}
