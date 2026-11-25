namespace Dockyard.Kind;

// kind's cloud provider, one container per engine while there are clusters: it gives LoadBalancer services an address
// and starts an Envoy container per service whose ports are published on the host, so they work from Windows and the Mac.
static class CloudProvider
{
    public const string Name = "dockyard-cloud-provider-kind";
    public const string Image = "registry.k8s.io/cloud-provider-kind/cloud-controller-manager:v0.12.0@sha256:1b97632e5dfec8f83c7e970cf18cedb9e6b707e272011d712cabf6ab59d323c9";
    public const string SetupLabel = "dockyard.cloud-provider-kind.setup";
    public static readonly string[] Flags = ["--enable-lb-port-mapping", "--enable-default-ingress=false", "--gateway-channel=disabled"];
    public static readonly string Setup = $"{Image} {string.Join(' ', Flags)} dockyard.service";

    public static string[] RunArgs(string context) =>
    [
        "--context", context, "run", "-d", "--name", Name, "--restart", "unless-stopped", "--network", "kind",
        "--label", $"{NfsStorage.ServiceLabel}=cloud-provider-kind", "--label", $"{SetupLabel}={Setup}", "-v", "/var/run/docker.sock:/var/run/docker.sock",
        Image, .. Flags,
    ];

    // For a cluster made without the dashboard open, which would otherwise start it on its next look at the clusters.
    public static async Task EnsureRunning(string context, Action<string>? onLine)
    {
        var (exit, output, _) = await SharedStorage.Docker(["--context", context, "inspect", "-f", $"{{{{.State.Running}}}} {{{{index .Config.Labels \"{SetupLabel}\"}}}}", Name]);
        if (exit == 0 && output.Trim() == $"true {Setup}") return;
        if (exit == 0) await SharedStorage.Docker(["--context", context, "rm", "-f", Name]);
        onLine?.Invoke("Starting kind's cloud provider, which gives LoadBalancer services an address");
        var run = await SharedStorage.Docker(RunArgs(context));
        if (run.ExitCode != 0) throw new InvalidOperationException($"Could not start kind's cloud provider: {run.Error.Trim()}");
    }
}