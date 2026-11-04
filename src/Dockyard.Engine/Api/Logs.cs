using System.Runtime.CompilerServices;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public enum LogStream { Stdout = 1, Stderr = 2 }

public readonly record struct LogLine(LogStream Stream, DateTimeOffset? Time, string Text);

public sealed record LogOptions(bool Follow = true, int? Tail = 1000, bool Tty = false);

public sealed partial class DockerClient
{
    public async IAsyncEnumerable<LogLine> LogsAsync(string id, LogOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = $"containers/{Esc(id)}/logs?stdout=1&stderr=1&timestamps=1&follow={Bool(options.Follow)}&tail={options.Tail?.ToString() ?? "all"}";
        using var res = await Send(HttpMethod.Get, path, null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var (kind, raw) in LogDemuxer.ReadAsync(stream, options.Tty, ct))
            yield return SplitTimestamp(kind, raw);
    }

    static LogLine SplitTimestamp(LogStream kind, string raw)
    {
        var space = raw.IndexOf(' ');
        if (space > 0 && RfcTime.Parse(raw.AsSpan(0, space)) is { } time)
            return new LogLine(kind, time, raw[(space + 1)..]);
        return new LogLine(kind, null, raw);
    }
}
