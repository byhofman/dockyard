namespace Dockyard.Kind;

// How kind and its cloud provider mark their containers.
static class KindLabels
{
    public const string Cluster = "io.x-k8s.kind.cluster";
    public const string LoadBalancerCluster = "io.x-k8s.cloud-provider-kind.cluster";
}
