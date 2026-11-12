using System.Collections.ObjectModel;
using Avalonia.Threading;
using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class LogsViewModel : Observable, IDisposable
{
    readonly LogBuffer buffer = new();
    readonly List<LogLine> incoming = [];
    readonly DispatcherTimer flushTimer;
    CancellationTokenSource? cts;
    bool follow = true, showTimestamps;
    string search = "";

    public LogsViewModel()
    {
        flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Flush());
    }

    public ObservableCollection<LogRow> Rows { get; } = [];
    public event Action? ScrollToEndRequested;

    public bool Follow
    {
        get => follow;
        set { if (Set(ref follow, value) && value) ScrollToEndRequested?.Invoke(); }
    }

    public bool ShowTimestamps
    {
        get => showTimestamps;
        set { if (Set(ref showTimestamps, value)) Rebuild(); }
    }

    public string Search
    {
        get => search;
        set { if (Set(ref search, value ?? "")) Rebuild(); }
    }

    public void Start(DockerClient client, string id, bool tty) => Start(client, [new LogSource(id, tty, null)]);

    public void Start(DockerClient client, IReadOnlyList<LogSource> sources)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        lock (incoming) incoming.Clear();
        buffer.Clear();
        Rows.Clear();
        var token = cts.Token;
        foreach (var source in sources) _ = Task.Run(() => Pump(client, source, token), token);
        flushTimer.Start();
    }

    async Task Pump(DockerClient client, LogSource source, CancellationToken ct)
    {
        try
        {
            await foreach (var line in client.LogsAsync(source.Id, new LogOptions(Follow: true, Tail: 1000, Tty: source.Tty), ct).ConfigureAwait(false))
            {
                var entry = source.Prefix is null ? line : line with { Text = $"{source.Prefix} | {line.Text}" };
                lock (incoming) incoming.Add(entry);
            }
        }
        catch (Exception)
        {
        }
    }

    void Flush()
    {
        List<LogLine> batch;
        lock (incoming)
        {
            if (incoming.Count == 0) return;
            batch = [.. incoming];
            incoming.Clear();
        }

        foreach (var line in batch)
        {
            buffer.Add(line);
            if (LogBuffer.Matches(line, search)) Rows.Add(ToRow(line));
        }
        var excess = Rows.Count - buffer.Capacity;
        for (var i = 0; i < excess; i++) Rows.RemoveAt(0);
        if (Follow) ScrollToEndRequested?.Invoke();
    }

    void Rebuild()
    {
        Rows.Clear();
        foreach (var line in buffer.Filtered(search)) Rows.Add(ToRow(line));
        if (Follow) ScrollToEndRequested?.Invoke();
    }

    LogRow ToRow(LogLine line) => new(
        ShowTimestamps && line.Time is { } t ? $"{t.ToLocalTime():HH:mm:ss.fff}  {line.Text}" : line.Text,
        line.Stream == LogStream.Stderr);

    public void Dispose()
    {
        flushTimer.Stop();
        cts?.Cancel();
        cts?.Dispose();
    }
}
