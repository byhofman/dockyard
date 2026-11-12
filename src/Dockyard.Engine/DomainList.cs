namespace Dockyard.Engine;

public sealed record DomainList(IReadOnlyList<DomainInfo> Domains, bool Https);
