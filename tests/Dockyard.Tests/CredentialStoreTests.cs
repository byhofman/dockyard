using System.Text.Json.Nodes;
using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class CredentialStoreTests
{
    static string B64(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s));

    [Fact]
    public void Uninstalling_says_which_logins_stay_in_the_store()
    {
        var notice = CredentialStore.LeftBehind("wincred", """{"https://ghcr.io":"me","docker.io":"me"}""");
        Assert.Contains("ghcr.io", notice);
        Assert.Contains("Windows Credential Manager", notice);
        Assert.Null(CredentialStore.LeftBehind("wincred", "{}"));
        Assert.Null(CredentialStore.LeftBehind("wincred", "not json"));
    }

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

        var back = EngineConfig.WithoutDockyard(merged, kept: null);
        Assert.DoesNotContain("198.19.250.1", back);
        Assert.Contains("journald", back);
    }

    // GitHub's runners come with {"dns":["168.63.129.16"]} on some machines, which used to leave containers without
    // host.docker.internal there.
    [Fact]
    public void Dns_someone_configured_becomes_the_agents_upstream_and_comes_back_on_uninstall()
    {
        const string theirs = """{"dns":["10.0.0.2","10.0.0.3"],"log-driver":"journald"}""";

        var merged = EngineConfig.WithDockyard(theirs);
        Assert.Equal(["198.19.250.1"], JsonNode.Parse(merged)!["dns"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("journald", merged);

        var kept = EngineConfig.KeptDns(theirs);
        Assert.Equal("nameserver 10.0.0.2\nnameserver 10.0.0.3\n", kept);

        var back = JsonNode.Parse(EngineConfig.WithoutDockyard(merged, kept))!;
        Assert.Equal(["10.0.0.2", "10.0.0.3"], back["dns"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("journald", back["log-driver"]!.GetValue<string>());
    }

    [Fact]
    public void Only_someone_elses_servers_are_kept()
    {
        Assert.Null(EngineConfig.KeptDns("{}"));
        Assert.Null(EngineConfig.KeptDns(EngineConfig.WithDockyard("{}")));
        Assert.Equal("nameserver 10.0.0.2\n", EngineConfig.KeptDns("""{"dns":["198.19.250.1","10.0.0.2"]}"""));
        Assert.DoesNotContain("dns", EngineConfig.WithoutDockyard(EngineConfig.WithDockyard("{}"), kept: null));
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

public sealed class EngineOptionsTests
{
    [Theory]
    [InlineData("https://mirror.example.com", true)]
    [InlineData("http://10.0.0.1:5000/", true)]
    [InlineData("mirror.example.com", false)]
    [InlineData("https://mirror.example.com/path", false)]
    [InlineData("ftp://mirror", false)]
    public void Mirrors_are_urls_with_only_a_host(string line, bool ok) => Assert.Equal(ok, EngineOptions.MirrorProblem(line) is null);

    [Theory]
    [InlineData("registry.example.com:5000", true)]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("localhost:5000", true)]
    [InlineData("http://registry", false)]
    [InlineData("bad host", false)]
    public void Insecure_registries_are_hosts_or_ranges(string line, bool ok) => Assert.Equal(ok, EngineOptions.InsecureRegistryProblem(line) is null);

    [Theory]
    [InlineData("10.20.0.0/16 24", true)]
    [InlineData("  172.30.0.0/16   20", true)]
    [InlineData("10.20.0.0/16", false)]
    [InlineData("10.20.0.0 24", false)]
    [InlineData("10.20.0.0/24 16", false)]
    [InlineData("10.20.0.0/16 31", false)]
    [InlineData("fd00::/8 64", false)]
    public void Pools_are_a_range_and_a_network_size_that_fits(string line, bool ok) => Assert.Equal(ok, EngineOptions.ParsePool(line.Trim()).Problem is null);

    [Fact]
    public void The_engine_file_carries_what_was_set_and_nothing_empty()
    {
        var settings = new Settings { RegistryMirrors = "https://m.example\n\n", InsecureRegistries = "r.example:5000\n", AddressPools = "10.20.0.0/16 24\n" };
        var config = System.Text.Json.Nodes.JsonNode.Parse(EngineConfig.Render(null, settings))!;
        Assert.Equal("https://m.example", config["registry-mirrors"]![0]!.GetValue<string>());
        Assert.Equal("r.example:5000", config["insecure-registries"]![0]!.GetValue<string>());
        Assert.Equal("10.20.0.0/16", config["default-address-pools"]![0]!["base"]!.GetValue<string>());
        Assert.Equal(24, config["default-address-pools"]![0]!["size"]!.GetValue<int>());

        var bare = EngineConfig.Render(null, new Settings());
        Assert.DoesNotContain("registry-mirrors", bare);
        Assert.DoesNotContain("default-address-pools", bare);
    }

    [Fact]
    public void All_problems_are_listed_at_once() =>
        Assert.Equal(3, EngineOptions.Problems("nope", "http://x", "1.2.3.4").Count);
}

public sealed class TrayContainersTests
{
    static Dockyard.Engine.ContainerSummary C(string name, string state, string? project = null, int? port = null, bool helper = false) => new()
    {
        Id = name,
        Names = ["/" + name],
        State = state,
        Labels = new Dictionary<string, string>(
            (project is null ? [] : new[] { KeyValuePair.Create("com.docker.compose.project", project) })
            .Concat(helper ? [KeyValuePair.Create("dockyard.helper", "1")] : [])),
        Ports = port is null ? [] : [new Dockyard.Engine.PortMapping { PublicPort = port, PrivatePort = 80, Type = "tcp" }],
    };

    [Fact]
    public void Running_containers_come_first_with_their_project_and_helpers_stay_out()
    {
        var items = TrayContainers.From([C("zeta", "exited"), C("web", "running", "shop", 8080), C("alpha", "running"), C("holder", "created", helper: true)]);
        Assert.Equal(["alpha", "web", "zeta"], items.Select(i => i.Name));
        Assert.Equal(8080, items[1].WebPort);
        Assert.Equal("shop", items[1].Project);
        Assert.False(items[2].Running);
    }

    [Fact]
    public void A_stopped_container_offers_no_address()
    {
        Assert.Null(TrayContainers.From([C("web", "exited", port: 8080)])[0].WebPort);
    }

    [Fact]
    public void The_same_list_compares_equal_so_an_open_menu_is_left_alone() =>
        Assert.True(TrayContainers.From([C("a", "running")]).SequenceEqual(TrayContainers.From([C("a", "running")])));

    [Fact]
    public void The_menu_stays_a_menu() =>
        Assert.Equal(TrayContainers.Limit, TrayContainers.From(Enumerable.Range(0, 50).Select(i => C($"c{i}", "running"))).Count);
}
