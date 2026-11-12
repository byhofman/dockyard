namespace Dockyard.Engine;

sealed class AgentMessage
{
    public string? Op { get; set; }
    public string? Container { get; set; }
    public int? Port { get; set; }
    public string? Cert { get; set; }
    public string? Key { get; set; }
    public string? Error { get; set; }
    public List<DomainInfo>? Domains { get; set; }
    public bool Https { get; set; }
}
