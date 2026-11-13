namespace Dockyard.Engine;

public sealed class DomainInfo
{
    public string ContainerId { get; set; } = "";
    public string Domain { get; set; } = "";
    public int? Port { get; set; }
    public bool PortIsChosen { get; set; }
}
