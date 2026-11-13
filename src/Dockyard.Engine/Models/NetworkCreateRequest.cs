namespace Dockyard.Engine;

sealed class NetworkCreateRequest
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
}
