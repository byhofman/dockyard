namespace Dockyard.Engine;

// kind's cloud provider, one container per engine while a running cluster wants a load balancer: it gives LoadBalancer
// services an address and starts an Envoy container per service whose ports are published on the host, so they work
// from Windows and the Mac.
public static class KindCloudProvider
{
    public const string Name = "dockyard-cloud-provider-kind";
    public const string Image = "registry.k8s.io/cloud-provider-kind/cloud-controller-manager:v0.12.0@sha256:1b97632e5dfec8f83c7e970cf18cedb9e6b707e272011d712cabf6ab59d323c9";
    public const string SetupLabel = "dockyard.cloud-provider-kind.setup";
    public const string ServiceLabel = "dockyard.service";
    public const string ClusterLabel = "io.x-k8s.cloud-provider-kind.cluster";
    public const string NameLabel = "io.x-k8s.cloud-provider-kind.loadbalancer.name";
    public const int EnvoyAdminPort = 10000;
    public static readonly string[] Flags = ["--enable-lb-port-mapping", "--enable-default-ingress=false", "--gateway-channel=disabled"];
    public static readonly string Setup = $"{Image} {string.Join(' ', Flags)} dockyard.service";

    public static ServiceContainerSpec Spec { get; } = new(Name, Image, Flags,
        new Dictionary<string, string> { [ServiceLabel] = "cloud-provider-kind", [SetupLabel] = Setup },
        ["/var/run/docker.sock:/var/run/docker.sock"], [], SetupLabel: SetupLabel);
}
