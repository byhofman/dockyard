using System.Net.Sockets;
using System.Text.Json;
using Dockyard.Engine;

namespace Dockyard.Agent.Tests;

public sealed class AgentTests
{
    [Theory]
    [InlineData("tcp 8080", "Tcp", 8080)]
    [InlineData("udp 53\r", "Udp", 53)]
    public void Parses_forward_headers(string line, string protocol, int port)
    {
        var header = ForwardHeader.Parse(line);
        Assert.NotNull(header);
        Assert.Equal(Enum.Parse<ForwardProtocol>(protocol), header.Value.Protocol);
        Assert.Equal(port, header.Value.Port);
        Assert.Equal(line.Trim(), header.Value.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("tcp")]
    [InlineData("sctp 80")]
    [InlineData("tcp 0")]
    [InlineData("tcp 70000")]
    public void Rejects_bad_forward_headers(string line) => Assert.Null(ForwardHeader.Parse(line));

    [Fact]
    public async Task Udp_frames_round_trip()
    {
        var stream = new MemoryStream();
        stream.Write(UdpFrame.Encode([1, 2, 3]));
        stream.Write(UdpFrame.Encode([]));
        stream.Position = 0;
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal([1, 2, 3], await UdpFrame.ReadAsync(stream, ct));
        Assert.Empty((await UdpFrame.ReadAsync(stream, ct))!);
        Assert.Null(await UdpFrame.ReadAsync(stream, ct));
    }

    [Fact]
    public void Published_ports_dedupe_and_prefer_wildcard_binds()
    {
        ContainerSummary C(string state, params (string Ip, int Port, string Type)[] ports) => new()
        {
            State = state,
            Ports = ports.Select(p => new PortMapping { IP = p.Ip, PrivatePort = 80, PublicPort = p.Port, Type = p.Type }).ToList(),
        };

        var result = PublishedPorts.From(
        [
            C("running", ("0.0.0.0", 8080, "tcp"), ("::", 8080, "tcp")),
            C("running", ("127.0.0.1", 5432, "tcp"), ("0.0.0.0", 53, "udp")),
            C("running", ("::1", 6379, "tcp")),
            C("exited", ("0.0.0.0", 9000, "tcp")),
        ]);

        Assert.Equal(
        [
            new PublishedPort("udp", "0.0.0.0", 53),
            new PublishedPort("tcp", "127.0.0.1", 5432),
            new PublishedPort("tcp", "127.0.0.1", 6379),
            new PublishedPort("tcp", "0.0.0.0", 8080),
        ], result);
    }

    [Fact]
    public void Parses_meminfo()
    {
        var info = MemoryInfo.Parse("MemTotal:        8123456 kB\nMemFree:         1000 kB\nMemAvailable:    4000000 kB\n");
        Assert.Equal(8123456, info.TotalKb);
        Assert.Equal(4000000, info.AvailableKb);
    }

    [Theory]
    [InlineData("BOOT_IMAGE=/vmlinuz console=hvc0 root=/dev/vda dockyard.agent=tcp\n", true)]
    [InlineData("console=hvc0 root=/dev/vda", false)]
    public void Reads_agent_mode_from_cmdline(string cmdline, bool tcp) => Assert.Equal(tcp, AgentOptions.FromCmdline(cmdline).Tcp);

    [Fact]
    public void Vsock_address_layout_matches_sockaddr_vm()
    {
        var address = new VsockEndPoint(3, 2375).Serialize();
        Assert.Equal(16, address.Size);
        Assert.Equal((AddressFamily)40, address.Family);
        Assert.Equal([0x47, 0x09, 0, 0], new[] { address[4], address[5], address[6], address[7] });
        Assert.Equal([3, 0, 0, 0], new[] { address[8], address[9], address[10], address[11] });

        var parsed = (VsockEndPoint)new VsockEndPoint(0, 0).Create(address);
        Assert.Equal(3u, parsed.Cid);
        Assert.Equal(2375u, parsed.Port);
    }

    [Fact]
    public void Control_messages_are_camel_case_without_nulls()
    {
        var json = JsonSerializer.Serialize(new ControlMessage { Type = "memory", TotalKb = 10, AvailableKb = 5 }, ControlJson.Default.ControlMessage);
        Assert.Equal("""{"type":"memory","totalKb":10,"availableKb":5}""", json);

        var ports = JsonSerializer.Serialize(new ControlMessage { Type = "ports", Ports = [new("tcp", "0.0.0.0", 80)] }, ControlJson.Default.ControlMessage);
        Assert.Equal("""{"type":"ports","ports":[{"protocol":"tcp","hostIp":"0.0.0.0","port":80}]}""", ports);
    }
}
