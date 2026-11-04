using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Store;

enum SessionState { Connecting, Connected, Reconnecting, Unavailable }

sealed class EngineSession : Observable
{
    static readonly string[] EventTypes = ["container", "image", "volume", "network"];

    readonly HashSet<string> pending = [];
    CancellationTokenSource? cts;
    DockerClient? client;
    SessionState state = SessionState.Connecting;
    string? error;
    string contextName = "";
    bool flushScheduled;

    public ContainerStore Containers { get; } = new();
    public DockerClient? Client => client;
    public bool IsWindowsEngine => client?.IsWindows == true;

    public SessionState State
    {
        get => state;
        private set { if (Set(ref state, value)) Raise(nameof(IsConnected), nameof(IsUnavailable), nameof(IsReconnecting)); }
    }

    public bool IsConnected => State == SessionState.Connected;
    public bool IsUnavailable => State == SessionState.Unavailable;
    public bool IsReconnecting => State == SessionState.Reconnecting;
    public string? Error { get => error; private set => Set(ref error, value); }

    public string ContextName
    {
        get => contextName;
        private set { if (Set(ref contextName, value)) Raise(nameof(EngineId)); }
    }

    public string? EngineId => ContextName is "linux" or "win" ? ContextName : null;

    public event Action? Connected;
    public event Action<RouteAction>? ResourceChanged;

    public void Start()
    {
        Stop();
        cts = new CancellationTokenSource();
        _ = Run(cts.Token);
    }

    public void Stop()
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = null;
    }

    async Task Run(CancellationToken ct)
    {
        TimeSpan? delay = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var context = DockerContext.Resolve();
                ContextName = context.Name;
                if (client is null || client.Endpoint != context.Endpoint)
                {
                    client?.Dispose();
                    client = new DockerClient(context.Endpoint);
                    Containers.Clear();
                }

                await client.PingAsync(ct);
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                Containers.Reset(await client.ListContainersAsync(all: true, ct));
                State = SessionState.Connected;
                Error = null;
                delay = null;
                Connected?.Invoke();

                await foreach (var e in client.EventsAsync(EventTypes, since, ct)) Handle(e);
                State = SessionState.Reconnecting;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                if (ex is EngineUnavailableException or DockerContextException or FormatException)
                {
                    State = SessionState.Unavailable;
                    Containers.Clear();
                }
                else State = SessionState.Reconnecting;
            }

            delay = Backoff.Next(delay);
            try { await Task.Delay(delay.Value, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    void Handle(DockerEvent e)
    {
        var route = EventRouter.Route(e);
        switch (route.Kind)
        {
            case RouteKind.Refresh:
                pending.Add(route.Id);
                _ = FlushSoon();
                break;
            case RouteKind.Remove:
                pending.Remove(route.Id);
                Containers.Remove(route.Id);
                break;
            case RouteKind.None:
                break;
            default:
                ResourceChanged?.Invoke(route);
                break;
        }
    }

    async Task FlushSoon()
    {
        if (flushScheduled) return;
        flushScheduled = true;
        await Task.Delay(100);
        flushScheduled = false;

        var ids = pending.ToList();
        pending.Clear();
        if (client is not { } c) return;
        foreach (var id in ids)
        {
            try
            {
                if (await c.FindContainerAsync(id) is { } summary) Containers.Upsert(summary);
                else Containers.Remove(id);
            }
            catch (Exception) when (c != client || State != SessionState.Connected)
            {
                return;
            }
            catch (DockerException)
            {
            }
        }
    }
}
