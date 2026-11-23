using Dockyard.Dashboard.Store;
using Dockyard.Engine;
using PublishedPort = Dockyard.Dashboard.Store.PublishedPort;

namespace Dockyard.Dashboard.Features.Clusters;

sealed record LoadBalancer(string Service, IReadOnlyList<PublishedPort> Ports)
{
    public string PortsText => string.Join(", ", Ports.Select(p => $"{p.ContainerPort}/{p.Protocol.ToLowerInvariant()}"));
}

// kind's cloud provider, one container per engine while there are clusters: it gives LoadBalancer services an address
// and starts an Envoy container per service whose ports are published on the host, so they work from Windows and the Mac.
static class LoadBalancers
{
    public const string Name = "dockyard-cloud-provider-kind";
    public const string Image = "registry.k8s.io/cloud-provider-kind/cloud-controller-manager:v0.12.0@sha256:1b97632e5dfec8f83c7e970cf18cedb9e6b707e272011d712cabf6ab59d323c9";
    public const string ClusterLabel = "io.x-k8s.cloud-provider-kind.cluster";
    public const string NameLabel = "io.x-k8s.cloud-provider-kind.loadbalancer.name";
    const string ImageLabel = "dockyard.cloud-provider-kind.image";
    const int EnvoyAdminPort = 10000;

    public static string[] RunArgs(string context) =>
    [
        "--context", context, "run", "-d", "--name", Name, "--restart", "unless-stopped", "--network", "kind",
        "--label", "dockyard.helper=cloud-provider-kind", "--label", $"{ImageLabel}={Image}", "-v", "/var/run/docker.sock:/var/run/docker.sock",
        Image, "--enable-lb-port-mapping",
    ];

    public static bool IsProxy(ContainerItem item) => item.Labels.ContainsKey(ClusterLabel);

    public static List<LoadBalancer> For(string cluster, IEnumerable<ContainerItem> containers) => containers
        .Where(c => c.Labels.GetValueOrDefault(ClusterLabel) == cluster)
        .Select(c => new LoadBalancer(
            c.Labels.GetValueOrDefault(NameLabel) is { } name && name.StartsWith(cluster + "/", StringComparison.Ordinal) ? name[(cluster.Length + 1)..] : c.Name,
            c.Ports.Where(p => p.ContainerPort != EnvoyAdminPort).OrderBy(p => p.ContainerPort).ToList()))
        .OrderBy(l => l.Service, StringComparer.Ordinal)
        .ToList();

    public enum Step { None, Run, Start, Replace, Remove }

    // What to do with the controller: one is wanted while clusters exist and the switch is on, and an older image is replaced.
    public static Step Plan(bool wanted, ContainerSummary? current) => (wanted, current) switch
    {
        (true, null) => Step.Run,
        (true, { } c) when c.Labels?.GetValueOrDefault(ImageLabel) != Image => Step.Replace,
        (true, { State: not "running" }) => Step.Start,
        (false, not null) => Step.Remove,
        _ => Step.None,
    };

    public static async Task Sync(DockerClient client, string context, bool wanted, Action<string>? onLine = null)
    {
        var current = (await client.ListContainersAsync(all: true)).FirstOrDefault(c => c.Names.Contains("/" + Name));
        var step = Plan(wanted, current);
        if (step is Step.Remove or Step.Replace) await client.RemoveContainerAsync(current!.Id, force: true);
        if (step is Step.Start) await client.StartContainerAsync(current!.Id);
        if (step is Step.Run or Step.Replace)
        {
            var exit = await Resources.CliRunner.Run(RunArgs(context), line => onLine?.Invoke(line));
            if (exit != 0) throw new InvalidOperationException($"Could not start kind's cloud provider (docker run exited with {exit}).");
        }
    }
}
