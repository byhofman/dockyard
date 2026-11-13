namespace Dockyard.Agent;

sealed class AgentReply
{
    public string? Error { get; set; }
    public List<DomainInfo>? Domains { get; set; }
    public bool Https { get; set; }
}
