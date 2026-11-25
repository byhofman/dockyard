namespace Dockyard.Dashboard.Store;

// A kind cluster in the containers list: its nodes and the load balancer proxies for its services, under one row.
sealed record ClusterRow(KindCluster Cluster, bool IsExpanded)
{
    public const string KeyPrefix = "kind:";
    public string Name => Cluster.Name;
    public string Key => KeyPrefix + Name;
    public string Summary => $"{Cluster.Nodes.Count(n => n.IsRunning)}/{Cluster.Nodes.Count} nodes running";
    public string Location => Cluster.KubernetesVersion is { } v ? $"kind cluster · Kubernetes {v}" : "kind cluster";
    public bool IsRunning => Cluster.Nodes.Any(n => n.IsRunning);
    public override string ToString() => Name;

    // KindCluster holds its nodes in a list, which a record compares by reference; the store keeps rows that did not change.
    public bool Equals(ClusterRow? other) => other is not null && Name == other.Name && IsExpanded == other.IsExpanded && Summary == other.Summary && Location == other.Location;
    public override int GetHashCode() => HashCode.Combine(Name, IsExpanded, Summary, Location);
}
