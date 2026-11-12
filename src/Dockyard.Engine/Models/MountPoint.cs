namespace Dockyard.Engine;

public sealed class MountPoint
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool RW { get; set; }
}
