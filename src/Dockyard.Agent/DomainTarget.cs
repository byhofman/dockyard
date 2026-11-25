using System.Net;

namespace Dockyard.Agent;

sealed record DomainTarget(string ContainerId, string ContainerName, string Domain, IPAddress? Address, int? Pid, IReadOnlyList<int> Exposed, int? Label, bool IsHostNetwork);

// A kind cluster's LoadBalancer services on 80 and 443, as kind's cloud provider runs them: an Envoy container each.
sealed record ClusterIngress(string Cluster, IPAddress? Http, IPAddress? Https);
