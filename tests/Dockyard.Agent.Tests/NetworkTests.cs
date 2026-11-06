using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent.Tests;

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

        Assert.Equal(4, once);
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
    public async Task The_dns_server_answers_local_names_and_passes_the_rest_on_over_udp_and_tcp()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The agent only runs on Linux, and on Windows a UDP socket can be reset by an earlier datagram");
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
