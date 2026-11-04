using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace Dockyard.Engine.Internal;

static class LogDemuxer
{
    public static IAsyncEnumerable<(LogStream Stream, string Text)> ReadAsync(Stream stream, bool tty, CancellationToken ct) =>
        tty ? ReadRaw(stream, ct) : ReadMultiplexed(stream, ct);

    static async IAsyncEnumerable<(LogStream Stream, string Text)> ReadRaw(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line) yield return (LogStream.Stdout, line);
    }

    // Each frame: [stream:1][0:3][size:4 big-endian][payload:size]. Lines may span frames.
    static async IAsyncEnumerable<(LogStream Stream, string Text)> ReadMultiplexed(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        var header = new byte[8];
        var outBuffer = new LineBuffer();
        var errBuffer = new LineBuffer();

        while (await Fill(stream, header, 8, ct))
        {
            var kind = header[0] == 2 ? LogStream.Stderr : LogStream.Stdout;
            var size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
            var payload = new byte[size];
            if (!await Fill(stream, payload, size, ct)) break;

            var buffer = kind == LogStream.Stderr ? errBuffer : outBuffer;
            foreach (var line in buffer.Append(payload)) yield return (kind, line);
        }

        if (outBuffer.Rest() is { } outRest) yield return (LogStream.Stdout, outRest);
        if (errBuffer.Rest() is { } errRest) yield return (LogStream.Stderr, errRest);
    }

    static async Task<bool> Fill(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    sealed class LineBuffer
    {
        readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        readonly StringBuilder pending = new();

        public List<string> Append(byte[] bytes)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
            var count = decoder.GetChars(bytes, 0, bytes.Length, chars, 0);
            pending.Append(chars, 0, count);

            var lines = new List<string>();
            var text = pending.ToString();
            var start = 0;
            int newline;
            while ((newline = text.IndexOf('\n', start)) >= 0)
            {
                lines.Add(text[start..newline].TrimEnd('\r'));
                start = newline + 1;
            }
            pending.Clear().Append(text, start, text.Length - start);
            return lines;
        }

        public string? Rest() => pending.Length > 0 ? pending.ToString() : null;
    }
}
