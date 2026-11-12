using System.Net;

namespace Dockyard.Agent;

sealed record DomainTarget(string ContainerId, string ContainerName, string Domain, IPAddress? Address, int? Pid, IReadOnlyList<int> Exposed, int? Label, bool IsHostNetwork);
