using System.Runtime.Versioning;
using Dockyard.Core;
using Dockyard.Engine;
using Dockyard.Engine.Transport;

namespace Dockyard.Hosts;

// What only the tray keeps running on Windows: published ports on loopback, and the relay for container domains.
[SupportedOSPlatform("windows")]
sealed class WindowsTrayServices
{
    CancellationTokenSource? running;
    DomainRelay? domainRelay;

    public bool Started => running is not null;

    public void Start(bool linuxEngineWanted, Func<Task<bool>> relayToEngine)
    {
        if (running is not null) return;
        running = new CancellationTokenSource();
        _ = FollowPublishedPorts(running.Token);
        // An engine this tray did not start, for example before the tray was replaced, gets its pipes again.
        if (linuxEngineWanted) _ = Task.Run(relayToEngine);
        if (linuxEngineWanted) StartDomainRelay();
    }

    public void StartDomainRelay()
    {
        if (domainRelay is not null) return;
        domainRelay = new DomainRelay(_ => { });
        domainRelay.Start();
    }

    public void StopDomainRelay()
    {
        domainRelay?.Dispose();
        domainRelay = null;
    }

    // The tray does not have to be what started the engine, and the engine comes and goes without telling it.
    static async Task FollowPublishedPorts(CancellationToken ct)
    {
        using var forwarder = new LoopbackForwarder(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var docker = new DockerClient(DockerEndpoint.Parse(Paths.LinuxHost));
                var since = DateTimeOffset.UtcNow.AddSeconds(-1);
                async Task Refresh() => forwarder.Apply(PublishedPorts.From(await docker.ListContainersAsync(all: false, ct))
                    .Where(p => p.Protocol == "tcp").Select(p => p.Port));
                await Refresh();
                await foreach (var e in docker.EventsAsync(["container"], since, ct))
                    if (e.Action is "start" or "die" or "destroy") await Refresh();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                forwarder.Apply([]);
            }
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }
}
