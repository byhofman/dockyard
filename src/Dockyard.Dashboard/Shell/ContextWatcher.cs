using Avalonia.Threading;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Shell;

sealed class ContextWatcher : IDisposable
{
    readonly FileSystemWatcher? watcher;
    readonly Action onChange;
    string? current;
    bool pending;

    public ContextWatcher(Action onChange)
    {
        this.onChange = onChange;
        current = Read();
        var dir = DockerContext.ConfigDir(Environment.GetEnvironmentVariable);
        if (!Directory.Exists(dir)) return;
        watcher = new FileSystemWatcher(dir, "config.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => Schedule();
        watcher.Created += (_, _) => Schedule();
        watcher.Renamed += (_, _) => Schedule();
        watcher.EnableRaisingEvents = true;
    }

    static string? Read()
    {
        try { return DockerContext.CurrentContextName(); }
        catch (Exception) { return null; }
    }

    void Schedule() => Dispatcher.UIThread.Post(() =>
    {
        if (pending) return;
        pending = true;
        DispatcherTimer.RunOnce(() =>
        {
            pending = false;
            var next = Read();
            if (next == current) return;
            current = next;
            onChange();
        }, TimeSpan.FromMilliseconds(200));
    });

    public void Dispose() => watcher?.Dispose();
}
