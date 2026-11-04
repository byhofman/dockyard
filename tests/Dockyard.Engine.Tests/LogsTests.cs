using System.Buffers.Binary;
using System.Text;
using Dockyard.Engine.Internal;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class LogsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static byte[] Frame(byte stream, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = stream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    static byte[] Frame(byte stream, string text) => Frame(stream, Encoding.UTF8.GetBytes(text));

    static async Task<List<(LogStream, string)>> Demux(byte[] bytes, bool tty)
    {
        var lines = new List<(LogStream, string)>();
        await foreach (var l in LogDemuxer.ReadAsync(new MemoryStream(bytes), tty, Ct)) lines.Add(l);
        return lines;
    }

    [Fact]
    public async Task Demuxes_frames_and_joins_partial_lines()
    {
        var bytes = Frame(1, "hello\nwor").Concat(Frame(1, "ld\n")).Concat(Frame(2, "oops\r\n")).Concat(Frame(1, "tail")).ToArray();
        List<(LogStream, string)> expected =
            [(LogStream.Stdout, "hello"), (LogStream.Stdout, "world"), (LogStream.Stderr, "oops"), (LogStream.Stdout, "tail")];
        Assert.Equal(expected, await Demux(bytes, tty: false));
    }

    [Fact]
    public async Task Multibyte_character_split_across_frames_survives()
    {
        var euro = Encoding.UTF8.GetBytes("€\n");
        var bytes = Frame(1, euro[..1]).Concat(Frame(1, euro[1..])).ToArray();
        List<(LogStream, string)> expected = [(LogStream.Stdout, "€")];
        Assert.Equal(expected, await Demux(bytes, tty: false));
    }

    [Fact]
    public async Task Tty_stream_is_raw_stdout()
    {
        List<(LogStream, string)> expected = [(LogStream.Stdout, "a"), (LogStream.Stdout, "b")];
        Assert.Equal(expected, await Demux(Encoding.UTF8.GetBytes("a\r\nb\n"), tty: true));
    }

    [Fact]
    public async Task LogsAsync_splits_timestamps()
    {
        var stub = StubHandler.WithPing().Bytes(Frame(1, "2026-11-04T10:01:02.123456789Z listening on :80\n"));
        using var client = stub.Client();

        var lines = new List<LogLine>();
        await foreach (var l in client.LogsAsync("abc", new LogOptions(Follow: false, Tail: 50), Ct)) lines.Add(l);

        Assert.Equal("/v1.47/containers/abc/logs?stdout=1&stderr=1&timestamps=1&follow=0&tail=50", stub.Path(1));
        var line = Assert.Single(lines);
        Assert.Equal("listening on :80", line.Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 10, 1, 2, TimeSpan.Zero).AddTicks(1234567), line.Time);
    }

    [Fact]
    public async Task LogsAsync_without_tail_requests_all()
    {
        var stub = StubHandler.WithPing().Bytes([]);
        using var client = stub.Client();
        await foreach (var _ in client.LogsAsync("abc", new LogOptions(Follow: true, Tail: null), Ct)) { }
        Assert.EndsWith("follow=1&tail=all", stub.Path(1));
    }
}
