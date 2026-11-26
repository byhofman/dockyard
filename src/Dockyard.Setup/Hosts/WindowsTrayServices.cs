using System.Runtime.Versioning;
using Dockyard.Shared;

namespace Dockyard.Hosts;

// What has to run on Windows itself (the engine's pipes, published ports on loopback, the relay for container domains)
// runs in dockyard-host: Dockyard.Setup only asks for it.
[SupportedOSPlatform("windows")]
sealed class WindowsTrayServices
{
    public bool Started { get; private set; }

    public void Start(bool linuxEngineWanted, Func<Task<bool>> relayToEngine)
    {
        if (Started) return;
        Started = true;
        // An engine that runs already, for example from before this process started, is attached by dockyard-host.
        if (linuxEngineWanted) _ = Task.Run(relayToEngine);
        if (linuxEngineWanted) StartDomainRelay();
    }

    public void StartDomainRelay() => _ = Task.Run(async () =>
    {
        if (await WslEngine.EnsureHost()) await HostPipe.Ask("domains on", TimeSpan.FromSeconds(10));
    });

    public void StopDomainRelay() => _ = HostPipe.Ask("domains off", TimeSpan.FromSeconds(10));
}
