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

enum AgentMode { Vm, TcpTest, Wsl, Host }

sealed record AgentOptions(AgentMode Mode)
{
    // dockyard.agent=tcp on the kernel command line serves the same ports over TCP (used by the QEMU boot test).
    public static AgentOptions From(IReadOnlyList<string> args, string cmdline)
    {
        if (args.Contains("--mode=wsl")) return new(AgentMode.Wsl);
        if (args.Contains("--mode=host")) return new(AgentMode.Host);
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

// What listens in the engine's own network namespace, which is where --network host containers put their servers.
static class ListeningPorts
{
    const string TcpListen = "0A";
    const string UdpUnconnected = "07";
    static readonly HashSet<string> ReachableV4 = ["00000000", "0100007F"];
    static readonly HashSet<string> ReachableV6 = ["00000000000000000000000000000000", "00000000000000000000000001000000"];

    // A line of /proc/net/tcp is "  0: 0100007F:1F90 00000000:0000 0A ...", with the port in hex. An address only
    // reachable inside (the agent's own, a bridge's) is nothing a server on the Mac's loopback could stand in for.
    public static IEnumerable<int> Parse(string procNet, bool udp)
    {
        foreach (var line in procNet.Split('\n').Skip(1))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || fields[3] != (udp ? UdpUnconnected : TcpListen)) continue;
            var local = fields[1].Split(':');
            if (local.Length != 2 || !(ReachableV4.Contains(local[0]) || ReachableV6.Contains(local[0]))) continue;
            if (int.TryParse(local[1], System.Globalization.NumberStyles.HexNumber, null, out var port) && port > 0) yield return port;
        }
    }

    // Always the Mac's loopback: --network host means the engine's Linux, and that says nothing about the LAN.
    public static IEnumerable<PublishedPort> Read(Func<string, string?> readFile, IReadOnlySet<int> own)
    {
        IEnumerable<PublishedPort> From(string file, string protocol, bool udp) =>
            readFile(file) is { } text ? Parse(text, udp).Where(p => !own.Contains(p)).Select(p => new PublishedPort(protocol, "127.0.0.1", p)) : [];
        return From("/proc/net/tcp", "tcp", false).Concat(From("/proc/net/tcp6", "tcp", false))
            .Concat(From("/proc/net/udp", "udp", true)).Concat(From("/proc/net/udp6", "udp", true)).Distinct();
    }

    // Published ports come first: they may bind more than loopback, and that stays their choice.
    public static List<PublishedPort> Merge(IEnumerable<PublishedPort> published, IEnumerable<PublishedPort> listening) =>
        published.Concat(listening).GroupBy(p => (p.Protocol, p.Port)).Select(g => g.First()).OrderBy(p => p.Port).ThenBy(p => p.Protocol).ToList();
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
