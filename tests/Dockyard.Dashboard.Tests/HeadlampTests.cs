using Dockyard.Kind;

namespace Dockyard.Dashboard.Tests;

public sealed class HeadlampTests
{
    [Fact]
    public void Headlamp_answers_at_the_cluster_s_dashboard_name_with_a_trusted_certificate_when_cert_manager_is_there()
    {
        var manifest = Headlamp.Manifest("dev", tls: true).Replace("\r\n", "\n");

        Assert.Contains("- host: dashboard.dev.dockyard.internal", manifest);
        Assert.Contains("cert-manager.io/cluster-issuer: dockyard", manifest);
        Assert.Contains("hosts: [dashboard.dev.dockyard.internal]", manifest);
        Assert.Contains("ingressClassName: traefik", manifest);
        Assert.Contains("name: cluster-admin", manifest);
    }

    [Fact]
    public void Without_cert_manager_it_is_plain_http()
    {
        var manifest = Headlamp.Manifest("dev", tls: false);

        Assert.DoesNotContain("cert-manager.io", manifest);
        Assert.DoesNotContain("tls:", manifest);
        Assert.Contains("headlamp", ClusterAddons.All.Select(a => a.Release));
    }
}
