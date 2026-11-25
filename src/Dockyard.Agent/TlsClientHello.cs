using System.Text;

namespace Dockyard.Agent;

// The server name a TLS client asks for, read from its ClientHello before any TLS is spoken, so a connection for a
// cluster's ingress can go on untouched, with the cluster's own certificate.
static class TlsClientHello
{
    public const int RecordHeader = 5;

    // The whole first record's length from its header, or null when it is not a TLS handshake.
    public static int? RecordLength(ReadOnlySpan<byte> header) =>
        header.Length >= RecordHeader && header[0] == 0x16 && header[1] == 0x03 ? RecordHeader + (header[3] << 8 | header[4]) : null;

    public static string? ServerName(ReadOnlySpan<byte> record)
    {
        try
        {
            var p = RecordHeader;
            if (record.Length < p + 4 || record[p] != 0x01) return null;
            p += 4 + 2 + 32;
            p += 1 + record[p];
            p += 2 + (record[p] << 8 | record[p + 1]);
            p += 1 + record[p];
            var end = p + 2 + (record[p] << 8 | record[p + 1]);
            p += 2;
            while (p + 4 <= end && p + 4 <= record.Length)
            {
                var type = record[p] << 8 | record[p + 1];
                var length = record[p + 2] << 8 | record[p + 3];
                p += 4;
                if (type == 0 && p + 5 <= record.Length && record[p + 2] == 0)
                {
                    var nameLength = record[p + 3] << 8 | record[p + 4];
                    return p + 5 + nameLength <= record.Length ? Encoding.ASCII.GetString(record.Slice(p + 5, nameLength)).ToLowerInvariant() : null;
                }
                p += length;
            }
            return null;
        }
        catch (IndexOutOfRangeException) { return null; }
    }
}

// What was read to find the server name, then the rest of the connection, for the TLS that is spoken here after all.
sealed class ReplayStream(byte[] first, Stream rest) : Stream
{
    int offset;

    public override int Read(byte[] buffer, int index, int count) => Read(buffer.AsSpan(index, count));

    public override int Read(Span<byte> buffer)
    {
        if (offset < first.Length)
        {
            var n = Math.Min(buffer.Length, first.Length - offset);
            first.AsSpan(offset, n).CopyTo(buffer);
            offset += n;
            return n;
        }
        return rest.Read(buffer);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (offset < first.Length) return Read(buffer.Span);
        return await rest.ReadAsync(buffer, ct);
    }

    public override Task<int> ReadAsync(byte[] buffer, int index, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(index, count), ct).AsTask();
    public override void Write(byte[] buffer, int index, int count) => rest.Write(buffer, index, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => rest.WriteAsync(buffer, ct);
    public override Task WriteAsync(byte[] buffer, int index, int count, CancellationToken ct) => rest.WriteAsync(buffer, index, count, ct);
    public override void Flush() => rest.Flush();
    public override Task FlushAsync(CancellationToken ct) => rest.FlushAsync(ct);
    public override bool CanRead => true;
    public override bool CanWrite => rest.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
