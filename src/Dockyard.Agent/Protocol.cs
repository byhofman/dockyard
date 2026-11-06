using System.Buffers.Binary;
using System.Net;
using System.Text.Json.Serialization;
using Dockyard.Engine;

namespace Dockyard.Agent;

// vsock ports the helper on the Mac connects to.
static class Ports
{
    public const int Docker = 2375;
    public const int Forward = 2376;
    public const int Control = 2377;
}

enum AgentMode { Vm, TcpTest, Wsl }

sealed record AgentOptions(AgentMode Mode)
{
    // dockyard.agent=tcp on the kernel command line serves the same ports over TCP (used by the QEMU boot test).
    public static AgentOptions From(IReadOnlyList<string> args, string cmdline)
    {
        if (args.Contains("--mode=wsl")) return new(AgentMode.Wsl);
        return new(cmdline.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Contains("dockyard.agent=tcp") ? AgentMode.TcpTest : AgentMode.Vm);
    }
}

enum ForwardProtocol { Tcp, Udp }

readonly record struct ForwardHeader(ForwardProtocol Protocol, int Port)
{
    public override string ToString() => $"{(Protocol == ForwardProtocol.Tcp ? "tcp" : "udp")} {Port}";

    public static ForwardHeader? Parse(string line)
    {
        var parts = line.Trim().Split(' ');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port) || port is < 1 or > 65535) return null;
        return parts[0] switch
        {
            "tcp" => new ForwardHeader(ForwardProtocol.Tcp, port),
            "udp" => new ForwardHeader(ForwardProtocol.Udp, port),
            _ => null,
        };
    }
}

// UDP datagrams over a stream: [length:2 big-endian][payload].
static class UdpFrame
{
    public const int MaxPayload = ushort.MaxValue;

    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("Datagram too large", nameof(payload));
        var frame = new byte[2 + payload.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(2));
        return frame;
    }

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[2];
        if (!await Fill(stream, header, ct)) return null;
        var payload = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
        return await Fill(stream, payload, ct) ? payload : null;
    }

    static async Task<bool> Fill(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

sealed record PublishedPort(string Protocol, string HostIp, int Port);

static class PublishedPorts
{
    // One entry per protocol and port; a wildcard bind wins over a loopback bind.
    public static List<PublishedPort> From(IEnumerable<ContainerSummary> containers) => containers
        .Where(c => c.State == "running")
        .SelectMany(c => c.Ports)
        .Where(p => p.PublicPort is > 0 && p.Type is "tcp" or "udp")
        .Select(p => new PublishedPort(p.Type, Normalize(p.IP), p.PublicPort!.Value))
        .GroupBy(p => (p.Protocol, p.Port))
        .Select(g => g.Any(p => p.HostIp == "0.0.0.0") ? g.First() with { HostIp = "0.0.0.0" } : g.First())
        .OrderBy(p => p.Port).ThenBy(p => p.Protocol)
        .ToList();

    static string Normalize(string? ip) => ip switch
    {
        null or "" or "0.0.0.0" or "::" => "0.0.0.0",
        "::1" => "127.0.0.1",
        _ => ip,
    };
}

readonly record struct MemoryInfo(long TotalKb, long AvailableKb)
{
    public static MemoryInfo Parse(string meminfo)
    {
        long Value(string key) => meminfo.Split('\n')
            .Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(l => long.Parse(l[(key.Length + 1)..].Trim().Split(' ')[0]))
            .FirstOrDefault();
        return new MemoryInfo(Value("MemTotal"), Value("MemAvailable"));
    }
}

sealed class ControlMessage
{
    public string Type { get; set; } = "";
    public List<PublishedPort>? Ports { get; set; }
    public long? TotalKb { get; set; }
    public long? AvailableKb { get; set; }
    public long? UnixNs { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlMessage))]
partial class ControlJson : JsonSerializerContext;
