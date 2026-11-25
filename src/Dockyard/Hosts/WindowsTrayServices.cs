using System.Runtime.Versioning;
using Dockyard.Shared;

namespace Dockyard.Hosts;

// What has to run on Windows itself (the engine's pipes, published ports on loopback, the relay for container domains)
// runs in dockyard-adapter: the Toolbox only asks for it.
[SupportedOSPlatform("windows")]
sealed class WindowsTrayServices
{
    public bool Started { get; private set; }

    public void Start(bool linuxEngineWanted, Func<Task<bool>> relayToEngine)
    {
        if (Started) return;
        Started = true;
        // An engine that runs already, for example from before the Toolbox started, is attached by the adapter.
        if (linuxEngineWanted) _ = Task.Run(relayToEngine);
        if (linuxEngineWanted) StartDomainRelay();
    }

    public void StartDomainRelay() => _ = Task.Run(async () =>
    {
        if (await WslEngine.EnsureAdapter()) await AdapterPipe.Ask("domains on", TimeSpan.FromSeconds(10));
    });

    public void StopDomainRelay() => _ = AdapterPipe.Ask("domains off", TimeSpan.FromSeconds(10));
}
