using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Containers;

sealed class StatsSampler(EngineSession session)
{
    readonly Dictionary<string, CancellationTokenSource> streams = new();
    readonly HashSet<string> visible = [];
    bool updateQueued;

    public void SetVisible(string id, bool isVisible)
    {
        if (isVisible ? visible.Add(id) : visible.Remove(id)) QueueUpdate();
    }

    void QueueUpdate()
    {
        if (updateQueued) return;
        updateQueued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            updateQueued = false;
            Update();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Update()
    {
        var running = session.Containers.Items.Where(i => i.IsRunning).Select(i => i.Id).ToList();
        var wanted = session.IsConnected ? StatsSelection.Choose(running, visible) : [];

        foreach (var id in streams.Keys.Where(id => !wanted.Contains(id)).ToList()) Cancel(id);
        foreach (var id in wanted.Where(id => !streams.ContainsKey(id)))
        {
            var cts = new CancellationTokenSource();
            streams[id] = cts;
            _ = Pump(id, cts);
        }
    }

    public void StopAll()
    {
        foreach (var id in streams.Keys.ToList()) Cancel(id);
    }

    void Cancel(string id)
    {
        if (!streams.Remove(id, out var cts)) return;
        cts.Cancel();
        cts.Dispose();
        session.Containers.Get(id)?.ClearUsage();
    }

    async Task Pump(string id, CancellationTokenSource cts)
    {
        if (session.Client is not { } client) return;
        try
        {
            await foreach (var usage in client.StatsAsync(id, cts.Token))
                session.Containers.Get(id)?.ApplyUsage(usage);
        }
        catch (Exception)
        {
        }
        finally
        {
            if (streams.TryGetValue(id, out var current) && current == cts) streams.Remove(id);
        }
    }
}
