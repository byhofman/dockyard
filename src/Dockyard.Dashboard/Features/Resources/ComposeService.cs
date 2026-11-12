using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Resources;

sealed record ComposeService(string Name, IReadOnlyList<ContainerItem> Containers)
{
    public int Running => Containers.Count(c => c.IsRunning);
    public string State => Containers.Count == 0 ? "not created" : $"{Running}/{Containers.Count} running";
}
