using Dockyard.Engine;

namespace Dockyard.Agent;

sealed class ControlMessage
{
    public string Type { get; set; } = "";
    public List<PublishedPort>? Ports { get; set; }
    public long? TotalKb { get; set; }
    public long? AvailableKb { get; set; }
    public long? UnixNs { get; set; }
}
