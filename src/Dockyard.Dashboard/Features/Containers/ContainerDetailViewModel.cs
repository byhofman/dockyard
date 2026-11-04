using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed class ContainerDetailViewModel : Observable, IDisposable
{
    readonly EngineSession session;
    readonly Action<string> showError;
    InspectViewModel? inspect;
    bool confirmRemove;
    bool logsExpanded;
    int selectedTab;
    bool? tty;
    bool disposed;

    public ContainerDetailViewModel(ContainersViewModel actions, EngineSession session, ContainerItem item, Action<string> showError)
    {
        Actions = actions;
        this.session = session;
        this.showError = showError;
        Item = item;
        AskRemoveCommand = new Command(() => { ConfirmRemove = true; return Task.CompletedTask; });
        CancelRemoveCommand = new Command(() => { ConfirmRemove = false; return Task.CompletedTask; });
        CloseCommand = new Command(() => { actions.CloseDetail(); return Task.CompletedTask; });
        item.PropertyChanged += OnItemChanged;
        _ = Load();
    }

    public ContainersViewModel Actions { get; }
    public ContainerItem Item { get; }
    public LogsViewModel Logs { get; } = new();
    public Command AskRemoveCommand { get; }
    public Command CancelRemoveCommand { get; }
    public Command CloseCommand { get; }

    public InspectViewModel? Inspect { get => inspect; private set => Set(ref inspect, value); }
    public bool ConfirmRemove { get => confirmRemove; set => Set(ref confirmRemove, value); }
    public bool LogsExpanded { get => logsExpanded; set => Set(ref logsExpanded, value); }
    public int SelectedTab { get => selectedTab; set => Set(ref selectedTab, value); }

    async Task Load()
    {
        if (session.Client is not { } client) return;
        try
        {
            var details = await client.InspectContainerAsync(Item.Id);
            if (disposed) return;
            tty = details.Config.Tty;
            Inspect = new InspectViewModel(details);
            Logs.Start(client, Item.Id, details.Config.Tty);
        }
        catch (Exception ex) when (!disposed)
        {
            showError($"{Item.Name}: {ex.Message}");
        }
    }

    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ContainerItem.State) || disposed) return;
        if (Item.IsRunning && session.Client is { } client && tty is { } isTty)
        {
            Logs.Start(client, Item.Id, isTty);
            _ = RefreshInspect(client);
        }
    }

    async Task RefreshInspect(DockerClient client)
    {
        try
        {
            var details = await client.InspectContainerAsync(Item.Id);
            if (!disposed) Inspect = new InspectViewModel(details);
        }
        catch (DockerException) { }
    }

    public void Dispose()
    {
        disposed = true;
        Item.PropertyChanged -= OnItemChanged;
        Logs.Dispose();
    }
}

sealed record LogRow(string Text, bool IsError);

sealed record LogSource(string Id, bool Tty, string? Prefix);

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

sealed record KeyValue(string Key, string Value);

sealed record InspectSection(string Title, IReadOnlyList<KeyValue> Items)
{
    public string CopyText => string.Join(Environment.NewLine, Items.Select(i => i.Key.Length > 0 ? $"{i.Key}={i.Value}" : i.Value));
}

sealed class InspectViewModel(ContainerInspect c)
{
    public IReadOnlyList<InspectSection> Sections { get; } = Build(c);

    static List<InspectSection> Build(ContainerInspect c)
    {
        var command = string.Join(' ', (c.Config.Entrypoint ?? []).Concat(c.Config.Cmd ?? []));
        List<InspectSection> sections =
        [
            new("General",
            [
                new("ID", c.Id),
                new("Image", c.Config.Image),
                new("Command", command),
                new("Status", c.State.Health is { } h ? $"{c.State.Status} ({h.Status})" : c.State.Status),
                new("Created", c.Created),
                new("Restart policy", c.HostConfig.RestartPolicy?.Name is { Length: > 0 } r ? r : "no"),
                .. (c.Config.WorkingDir is { Length: > 0 } wd ? new[] { new KeyValue("Working dir", wd) } : []),
            ]),
        ];

        var ports = (c.NetworkSettings.Ports ?? [])
            .SelectMany(p => (p.Value ?? []).Select(b => new KeyValue(p.Key, $"{(b.HostIp is "" or "0.0.0.0" ? "*" : b.HostIp)}:{b.HostPort}")))
            .DistinctBy(k => (k.Key, k.Value.TrimStart('*', ':', '[', ']')))
            .ToList();
        if (ports.Count > 0) sections.Add(new("Ports", ports));

        var env = (c.Config.Env ?? []).Select(e =>
        {
            var eq = e.IndexOf('=');
            return eq < 0 ? new KeyValue(e, "") : new KeyValue(e[..eq], e[(eq + 1)..]);
        }).ToList();
        if (env.Count > 0) sections.Add(new("Environment", env));

        var mounts = c.Mounts.Select(m => new KeyValue(m.Destination, $"{(m.Type == "volume" ? m.Name : m.Source)} ({m.Type}, {(m.RW ? "rw" : "ro")})")).ToList();
        if (mounts.Count > 0) sections.Add(new("Mounts", mounts));

        var networks = (c.NetworkSettings.Networks ?? []).Select(n => new KeyValue(n.Key, n.Value.IPAddress)).ToList();
        if (networks.Count > 0) sections.Add(new("Networks", networks));

        var labels = (c.Config.Labels ?? []).OrderBy(l => l.Key).Select(l => new KeyValue(l.Key, l.Value)).ToList();
        if (labels.Count > 0) sections.Add(new("Labels", labels));

        return sections;
    }
}
