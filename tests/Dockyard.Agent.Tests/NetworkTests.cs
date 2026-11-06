using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent.Tests;

public sealed class DomainTests
{
    [Theory]
    [InlineData("web", null, null, new[] { "web.dockyard.test" })]
    [InlineData("Shop_Web-1", "shop", "web", new[] { "shop-web-1.dockyard.test", "shop-web.dockyard.test" })]
    [InlineData("__", null, null, new string[0])]
    public void Names_are_flat_and_lowercase_under_the_zone(string name, string? project, string? service, string[] expected) =>
        Assert.Equal(expected, DomainNames.For(name, project, service));

    [Theory]
    [InlineData(1234, null, new[] { 80 }, new[] { 80 }, 1234)]
    [InlineData(null, 8081, new[] { 80 }, new[] { 80 }, 8081)]
    [InlineData(null, null, new[] { 9229, 3000 }, new[] { 80 }, 3000)]
    [InlineData(null, null, new int[0], new[] { 9229, 8080 }, 8080)]
    [InlineData(null, null, new[] { 9229, 7000 }, new[] { 80 }, 80)]
    [InlineData(null, null, new[] { 9229, 7000 }, new int[0], 7000)]
    [InlineData(null, null, new int[0], new[] { 5432 }, 5432)]
    public void The_port_is_chosen_then_labelled_then_a_web_port_then_the_lowest(int? chosen, int? label, int[] listening, int[] exposed, int expected) =>
        Assert.Equal(expected, DomainNames.PickPort(chosen, label, listening, exposed));

    [Fact]
    public void Nothing_to_go_on_means_no_port() => Assert.Null(DomainNames.PickPort(null, null, [], []));

    [Fact]
    public void A_label_names_the_port_and_orbstacks_label_counts_too()
    {
        Assert.Equal(3000, DomainNames.LabelPort(new Dictionary<string, string> { ["dockyard.port"] = "3000" }));
        Assert.Equal(3001, DomainNames.LabelPort(new Dictionary<string, string> { ["dev.orbstack.http-port"] = "3001" }));
        Assert.Null(DomainNames.LabelPort(new Dictionary<string, string> { ["dockyard.port"] = "nope" }));
        Assert.Null(DomainNames.LabelPort(null));
    }

    [Theory]
    [InlineData("GET / HTTP/1.1\r\nHost: Web.Dockyard.Test:8080\r\n\r\n", "web.dockyard.test")]
    [InlineData("GET / HTTP/1.1\r\nhost:web.dockyard.test\r\nAccept: x\r\n\r\n", "web.dockyard.test")]
    [InlineData("GET / HTTP/1.0\r\n\r\n", null)]
    public void The_container_comes_from_the_host_header(string head, string? expected) =>
        Assert.Equal(expected, HttpHead.Host(System.Text.Encoding.ASCII.GetBytes(head)));

    [Fact]
    public void The_app_behind_the_proxy_is_told_how_it_was_reached()
    {
        var head = System.Text.Encoding.ASCII.GetBytes("GET /x HTTP/1.1\r\nHost: a\r\n\r\n");
        var text = System.Text.Encoding.ASCII.GetString(HttpHead.WithForwarded(head, "https", "a"));
        Assert.StartsWith("GET /x HTTP/1.1\r\nX-Forwarded-Proto: https\r\nX-Forwarded-Host: a\r\nHost: a\r\n\r\n", text);
    }

    [Fact]
    public async Task A_zone_name_nothing_answers_to_does_not_go_to_the_hosts_dns()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dns = new DnsServer(IPAddress.Loopback, _ => null, () => "nameserver 192.0.2.1\n", _ => { }, port: 0, ownedZone: DomainNames.InZone);
        var running = dns.Run(cts.Token);
        for (var i = 0; dns.Port == 0 && i < 100; i++) await Task.Delay(10, ct);
        await Task.Delay(50, ct);

        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await udp.ConnectAsync(IPAddress.Loopback, dns.Port, ct);
        var query = new List<byte> { 0, 7, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in "gone.dockyard.test".Split('.')) { query.Add((byte)label.Length); query.AddRange(System.Text.Encoding.ASCII.GetBytes(label)); }
        query.AddRange([0, 0, 1, 0, 1]);
        var buffer = new byte[512];
        // Windows reports a datagram that found no socket receiving yet as a reset of this one.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await udp.SendAsync(query.ToArray(), ct);
                await udp.ReceiveAsync(buffer, ct);
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionReset && attempt < 20) { await Task.Delay(50, ct); }
        }
        Assert.Equal(DnsServer.NameError, buffer[3] & 0x0F);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public void The_api_answers_and_refuses_what_it_does_not_know()
    {
        var table = new DomainTable(() => throw new InvalidOperationException(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => { });
        var api = new AgentApi(table, new DomainProxy(table, _ => { }));
        Assert.Empty(api.Reply("""{"op":"domains"}""").Domains!);
        Assert.NotNull(api.Reply("""{"op":"nope"}""").Error);
        Assert.NotNull(api.Reply("not json").Error);
        Assert.NotNull(api.Reply("""{"op":"tls","cert":"x","key":"y"}""").Error);
    }
}

public sealed class NetworkTests
{
    static byte[] Query(string name, ushort type, byte id = 7)
    {
        var bytes = new List<byte> { 0, id, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        bytes.AddRange([0, 0, (byte)type, 0, 1]);
        return [.. bytes];
    }

    [Fact]
    public void A_question_is_read_lowercase()
    {
        var question = DnsPacket.Question(Query("Host.Docker.Internal", DnsQuestion.A));
        Assert.NotNull(question);
        Assert.Equal("host.docker.internal", question.Value.Name);
        Assert.Equal(DnsQuestion.A, question.Value.Type);
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 1, 0 })]
    [InlineData(new byte[] { 0, 1, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 200, 1, 2 })]
    [InlineData(new byte[] { 0, 1, 1, 0, 0, 2, 0, 0, 0, 0, 0, 0, 1, 97, 0, 0, 1, 0, 1 })]
    public void Malformed_packets_are_not_answered(byte[] packet) => Assert.Null(DnsPacket.Question(packet));

    [Fact]
    public void An_a_answer_carries_the_address_and_the_query_id()
    {
        var query = Query("host.docker.internal", DnsQuestion.A, id: 42);
        var reply = DnsPacket.Answer(query, DnsPacket.Question(query)!.Value, GuestAddresses.Host);
        Assert.Equal(42, reply[1]);
        Assert.Equal(0x80, reply[2] & 0x80);
        Assert.Equal(1, reply[7]);
        Assert.Equal(GuestAddresses.Host, new IPAddress(reply[^4..]));
    }

    [Fact]
    public void An_aaaa_question_about_a_known_name_gets_an_empty_answer_not_an_error()
    {
        var query = Query("host.docker.internal", 28);
        var reply = DnsPacket.Answer(query, DnsPacket.Question(query)!.Value, GuestAddresses.Host);
        Assert.Equal(0, reply[3] & 0x0F);
        Assert.Equal(0, reply[7]);
    }

    [Fact]
    public void Upstreams_come_from_resolv_conf_without_the_agent_itself()
    {
        var upstreams = ResolvConf.Upstreams("# comment\nsearch lan\nnameserver 198.19.250.1\nnameserver  192.168.64.1\nnameserver\tfe80::1\nnameserver bogus\n", GuestAddresses.Dns);
        Assert.Equal([IPAddress.Parse("192.168.64.1"), IPAddress.Parse("fe80::1")], upstreams);
    }

    [Fact]
    public void Wsl_reaches_windows_through_loopback_when_mirrored_and_the_gateway_otherwise()
    {
        const string route = "Iface\tDestination\tGateway\tFlags\neth0\t00000000\t0101A8C0\t0003\neth0\t0001A8C0\t00000000\t0001\n";
        Assert.Equal(IPAddress.Loopback, WslHost.Address(true, route));
        Assert.Equal(IPAddress.Parse("192.168.1.1"), WslHost.Address(false, route));
        Assert.Equal(IPAddress.Loopback, WslHost.Address(false, "Iface\tDestination\n"));
    }

    [Fact]
    public async Task A_container_cannot_reach_the_engines_port_through_the_host_address()
    {
        var dialer = new TcpHostDialer(IPAddress.Loopback, WslHost.Refused);
        await Assert.ThrowsAsync<SocketException>(() => dialer.Dial(2375, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("--mode=wsl", "", "Wsl")]
    [InlineData("", "root=/dev/vda dockyard.agent=tcp quiet", "TcpTest")]
    [InlineData("", "root=/dev/vda quiet", "Vm")]
    public void The_mode_comes_from_the_arguments_or_the_kernel(string arg, string cmdline, string mode) =>
        Assert.Equal(Enum.Parse<AgentMode>(mode), AgentOptions.From(arg.Length > 0 ? [arg] : [], cmdline).Mode);

    [Fact]
    public void Listening_servers_are_found_on_wildcard_and_loopback_only()
    {
        const string tcp = """
              sl  local_address rem_address   st tx_queue
               0: 00000000:1F90 00000000:0000 0A 0
               1: 0100007F:0BB8 00000000:0000 0A 0
               2: 01FA13C6:0035 00000000:0000 0A 0
               3: 0100007F:1F41 0200A8C0:D431 01 0
            """;
        Assert.Equal([8080, 3000], ListeningPorts.Parse(tcp.ReplaceLineEndings("\n"), udp: false));
        const string tcp6 = "header\n 0: 00000000000000000000000001000000:1389 00000000000000000000000000000000:0000 0A 0\n";
        Assert.Equal([5001], ListeningPorts.Parse(tcp6, udp: false));
        const string udp = "header\n 0: 00000000:14E9 00000000:0000 07 0\n 1: 00000000:14EA 0100007F:0035 01 0\n";
        Assert.Equal([5353], ListeningPorts.Parse(udp, udp: true));
    }

    [Fact]
    public void Listening_servers_go_to_the_macs_loopback_and_published_ports_keep_their_bind()
    {
        var files = new Dictionary<string, string>
        {
            ["/proc/net/tcp"] = "h\n 0: 00000000:1F90 00000000:0000 0A 0\n 1: 00000000:094A 00000000:0000 0A 0\n 2: 00000000:0050 00000000:0000 0A 0\n",
            ["/proc/net/tcp6"] = "h\n 0: 00000000000000000000000000000000:1F90 00000000000000000000000000000000:0000 0A 0\n",
        };
        var listening = ListeningPorts.Read(f => files.GetValueOrDefault(f), new HashSet<int> { 2378 }).ToList();
        Assert.Equal(2, listening.Count);
        Assert.All(listening, p => Assert.Equal("127.0.0.1", p.HostIp));

        var merged = ListeningPorts.Merge([new Dockyard.Engine.PublishedPort("tcp", "0.0.0.0", 80)], listening);
        Assert.Equal([80, 8080], merged.Select(p => p.Port));
        Assert.Equal("0.0.0.0", merged[0].HostIp);
    }

    [Fact]
    public void Only_the_documented_names_mean_the_host()
    {
        Assert.Equal(GuestAddresses.Host, GuestAddresses.Resolve("host.docker.internal"));
        Assert.Equal(GuestAddresses.Host, GuestAddresses.Resolve("gateway.docker.internal"));
        Assert.Null(GuestAddresses.Resolve("example.com"));
    }

    [Fact]
    public async Task Rules_are_added_once_however_often_the_agent_restarts()
    {
        List<string> added = [];
        Task<int> Run(string file, string[] args)
        {
            var command = string.Join(' ', args);
            if (command.Contains("-C ")) return Task.FromResult(added.Contains(command.Replace("-C ", "-I ")) ? 0 : 1);
            if (command.Contains("-I ")) added.Add(command);
            return Task.FromResult(0);
        }

        await NetworkRules.Apply(Run, _ => { });
        var once = added.Count;
        await NetworkRules.Apply(Run, _ => { });

        Assert.True(once > 4);
        Assert.Equal(once, added.Count);
    }

    [Fact]
    public void Lan_traffic_cannot_reach_the_guest_addresses()
    {
        var commands = NetworkRules.Commands().Select(c => string.Join(' ', c)).ToList();
        Assert.Contains("iptables -I INPUT -d 198.19.250.0/24 -j DOCKYARD-INPUT", commands);
        Assert.All(commands.Where(c => c.Contains("PREROUTING")), c => Assert.Matches("-i (docker\\+|br-\\+) ", c));
    }

    [Fact]
    public void On_a_linux_host_redirected_traffic_gets_past_a_firewall_on_container_interfaces_only()
    {
        var commands = NetworkRules.HostCommands().Select(c => string.Join(' ', c)).ToList();
        var accepts = commands.Where(c => c.Contains("-j ACCEPT")).ToList();
        Assert.Equal(2, accepts.Count);
        Assert.All(accepts, c => Assert.Matches("-i (docker\\+|br-\\+) ", c));
        Assert.All(accepts, c => Assert.DoesNotContain("2375", c));
        Assert.DoesNotContain(NetworkRules.Commands().Select(c => string.Join(' ', c)), c => c.Contains("-j ACCEPT"));
    }

    [Fact]
    public void On_a_linux_host_containers_reach_dns_without_holding_port_53()
    {
        var commands = NetworkRules.HostCommands().Select(c => string.Join(' ', c)).ToList();
        Assert.Contains($"iptables -t nat -I PREROUTING -i docker+ -d 198.19.250.1 -p udp --dport 53 -j DNAT --to-destination 198.19.250.1:{GuestAddresses.ContainerDnsPort}", commands);
        Assert.Contains($"iptables -t nat -I OUTPUT -d 198.19.250.1 -p tcp --dport 53 -j DNAT --to-destination 198.19.250.1:{GuestAddresses.ContainerDnsPort}", commands);
    }

    [Fact]
    public async Task The_dns_server_answers_local_names_and_passes_the_rest_on_over_udp_and_tcp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dns = new DnsServer(IPAddress.Loopback, GuestAddresses.Resolve, () => "", _ => { }, port: 0);
        var running = dns.Run(cts.Token);
        for (var i = 0; dns.Port == 0 && i < 100; i++) await Task.Delay(10, ct);
        await Task.Delay(50, ct);

        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await udp.ConnectAsync(IPAddress.Loopback, dns.Port, ct);
        await udp.SendAsync(Query("host.docker.internal", DnsQuestion.A), ct);
        var buffer = new byte[512];
        var n = await udp.ReceiveAsync(buffer, ct);
        Assert.Equal(GuestAddresses.Host, new IPAddress(buffer[(n - 4)..n]));

        await udp.SendAsync(Query("example.com", DnsQuestion.A), ct);
        n = await udp.ReceiveAsync(buffer, ct);
        Assert.Equal(DnsServer.ServerFailure, buffer[3] & 0x0F);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, dns.Port, ct);
        await tcp.GetStream().WriteAsync(UdpFrame.Encode(Query("gateway.docker.internal", DnsQuestion.A)), ct);
        var reply = await UdpFrame.ReadAsync(tcp.GetStream(), ct);
        Assert.Equal(GuestAddresses.Host, new IPAddress(reply![^4..]));

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    sealed class FixedDialer(int target) : IHostDialer
    {
        public List<int> Dialed { get; } = [];

        public async Task<IConnection> Dial(int port, CancellationToken ct)
        {
            Dialed.Add(port);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Loopback, target, ct);
            return new SocketConnection(socket);
        }
    }

    [Fact]
    public async Task The_host_proxy_passes_on_redirected_connections_and_drops_direct_ones()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var service = new TcpListener(IPAddress.Loopback, 0);
        service.Start();
        var dialer = new FixedDialer(((IPEndPoint)service.LocalEndpoint).Port);
        IPEndPoint? destination = new(GuestAddresses.Host, 5173);
        var proxy = new HostProxy(dialer, _ => destination, _ => { }, port: 0);
        var running = proxy.Run(cts.Token);
        for (var i = 0; proxy.Port == 0 && i < 100; i++) await Task.Delay(10, ct);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, ct);
            using var accepted = await service.AcceptTcpClientAsync(ct);
            await client.GetStream().WriteAsync("ping"u8.ToArray(), ct);
            var buffer = new byte[4];
            await accepted.GetStream().ReadExactlyAsync(buffer, ct);
            Assert.Equal("ping"u8.ToArray(), buffer);
        }
        Assert.Equal([5173], dialer.Dialed);

        destination = null;
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy.Port, ct);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1], ct));
        }
        Assert.Equal([5173], dialer.Dialed);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }
}
