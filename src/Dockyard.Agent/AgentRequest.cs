namespace Dockyard.Agent;

// One JSON request and one JSON reply per line. Only the app and the dashboard get here: over vsock on macOS, on
// loopback in WSL, on a socket for the docker group on a Linux host.
sealed class AgentRequest
{
    public string Op { get; set; } = "";
    public string? Container { get; set; }
    public int? Port { get; set; }
    public string? Cert { get; set; }
    public string? Key { get; set; }
}
