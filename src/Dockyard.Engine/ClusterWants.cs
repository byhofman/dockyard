namespace Dockyard.Engine;

// What the host wants per cluster, from its definition, which the engine's agent cannot read: kind's cloud provider for
// its LoadBalancer services, these ports on the host's 127.0.0.1, and dockyard-nfs.
public sealed record ClusterWant(string Cluster, bool LoadBalancer, IReadOnlyList<int> Ports, bool Nfs);

public sealed record ClusterForward(int Port, string Service);

public sealed record TakenPort(int Port, string Cluster);

// What the agent made of a cluster's ports: those forwarded, those another cluster had first, and those another program
// holds on the host.
public sealed record ClusterStatus(string Cluster, IReadOnlyList<ClusterForward> Forwards, IReadOnlyList<TakenPort> Taken, IReadOnlyList<int> Busy);

public interface IClusterAgent
{
    Task SetClusterWantsAsync(IReadOnlyList<ClusterWant> wants, CancellationToken ct = default);
    Task<IReadOnlyList<ClusterStatus>> ClusterStatusAsync(CancellationToken ct = default);
}
