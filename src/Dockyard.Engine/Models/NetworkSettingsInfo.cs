namespace Dockyard.Engine;

public sealed class NetworkSettingsInfo
{
    public Dictionary<string, List<PortBinding>?>? Ports { get; set; }
    public Dictionary<string, EndpointSettings>? Networks { get; set; }
}
