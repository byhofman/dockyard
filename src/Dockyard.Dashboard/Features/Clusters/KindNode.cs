namespace Dockyard.Dashboard.Features.Clusters;

sealed record KindNode(string Name, string Role, string State)
{
    public bool IsRunning => State == "running";
}
