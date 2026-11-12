using System.Buffers.Binary;

namespace Dockyard.Agent;

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
