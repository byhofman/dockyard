namespace Dockyard.Kind;

sealed record KindNode(string Name, string Role, string State)
{
    public bool IsRunning => State == "running";
}
