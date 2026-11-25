namespace Dockyard.Dashboard.Features.Clusters;

sealed record KindCluster(string Name, IReadOnlyList<KindNode> Nodes, string? KubernetesVersion, int? ApiPort, bool HasSharedStorage = false)
{
    public string Context => "kind-" + Name;
    public bool IsRunning => Nodes.All(n => n.IsRunning);
    public string StateText => IsRunning ? "Running" : Nodes.Any(n => n.IsRunning) ? "Partly stopped" : "Stopped";
    public string NodesText => Nodes.Count == 1 ? "1 node" : $"{Nodes.Count} nodes";
    public string? ApiServer => IsRunning && ApiPort is { } port ? $"https://127.0.0.1:{port}" : null;
    public override string ToString() => Name;
    public string Summary => string.Join("  ·  ", new[] { KubernetesVersion, NodesText, StateText }.Where(s => s is not null));
    public string ToggleText => IsRunning ? "Stop" : "Start";

    // The API server and the load balancer in front of several come up first, so the workers find them when they start.
    public IEnumerable<KindNode> StartOrder => Nodes.OrderBy(n => n.Role is "external-load-balancer" or "control-plane" ? 0 : 1);
}
