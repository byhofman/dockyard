namespace Dockyard.Engine;

public sealed class ContainerSummary
{
    public HostConfigSummary? HostConfig { get; set; }
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public string ImageID { get; set; } = "";
    public string Command { get; set; } = "";
    public long Created { get; set; }
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public List<PortMapping> Ports { get; set; } = [];
    public Dictionary<string, string>? Labels { get; set; }
    public List<MountPoint>? Mounts { get; set; }

    public string Name => Names.Count > 0 ? Names[0].TrimStart('/') : Id[..Math.Min(12, Id.Length)];
}
