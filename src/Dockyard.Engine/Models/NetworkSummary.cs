namespace Dockyard.Engine;

public sealed class NetworkSummary
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Scope { get; set; } = "";
    public bool Internal { get; set; }
    public string? Created { get; set; }
    public Ipam? IPAM { get; set; }
    public Dictionary<string, NetworkContainer>? Containers { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}
