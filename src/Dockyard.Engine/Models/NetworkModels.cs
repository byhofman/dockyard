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

public sealed class Ipam
{
    public List<IpamConfig>? Config { get; set; }
}

public sealed class IpamConfig
{
    public string? Subnet { get; set; }
    public string? Gateway { get; set; }
}

public sealed class NetworkContainer
{
    public string Name { get; set; } = "";
    public string IPv4Address { get; set; } = "";
}

sealed class NetworkCreateRequest
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
}

sealed class NetworkCreateResponse
{
    public string Id { get; set; } = "";
}

sealed class NetworkConnectRequest
{
    public string Container { get; set; } = "";
}

sealed class NetworkDisconnectRequest
{
    public string Container { get; set; } = "";
    public bool Force { get; set; }
}
