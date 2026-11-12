namespace Dockyard.Agent;

sealed record DomainInfo(string ContainerId, string Domain, int? Port, bool PortIsChosen);
