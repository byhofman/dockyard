namespace Dockyard.Engine;

sealed class NetworkDisconnectRequest
{
    public string Container { get; set; } = "";
    public bool Force { get; set; }
}
