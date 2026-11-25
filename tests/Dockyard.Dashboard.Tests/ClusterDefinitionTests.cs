using Dockyard.Dashboard.Features.Clusters;

namespace Dockyard.Dashboard.Tests;

public sealed class ClusterDefinitionTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-cluster-definitions").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void What_the_form_writes_is_a_definition_kind_takes()
    {
        Assert.Null(ClusterDefinitions.Problem(KindCli.Config(0)));
        Assert.Null(ClusterDefinitions.Problem(KindCli.Config(2, sharedStorage: true)));
    }

    [Theory]
    [InlineData("kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: worker\n", "control-plane")]
    [InlineData("kind: Cluster\napiVersion: v1\n", "apiVersion")]
    [InlineData("apiVersion: kind.x-k8s.io/v1alpha4\n", "kind: Cluster")]
    [InlineData("kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: control-plane\n- role: master\n", "master")]
    [InlineData("kind: Cluster\n  apiVersion: [\n", "Line")]
    public void A_definition_kind_would_refuse_says_why(string yaml, string expected) =>
        Assert.Contains(expected, ClusterDefinitions.Problem(yaml));

    [Fact]
    public void A_definition_is_kept_with_its_add_ons_and_storage_and_goes_with_the_cluster()
    {
        var yaml = KindCli.Config(1) + "networking:\n  podSubnet: 10.244.0.0/16\n";

        ClusterDefinitions.Save("dev", new ClusterDefinition(yaml, ClusterStorage.Nfs, ["metrics-server", "traefik"], LoadBalancer: false, Ports: [8080, 8443]), dir);
        var loaded = ClusterDefinitions.Load("dev", dir)!;

        Assert.Equal(yaml, loaded.Yaml);
        Assert.Equal(ClusterStorage.Nfs, loaded.Storage);
        Assert.False(loaded.LoadBalancer);
        Assert.Equal([8080, 8443], loaded.Ports);
        Assert.Empty(loaded.LoadBalancerPorts);
        Assert.Equal([ClusterAddons.MetricsServer, ClusterAddons.Traefik], loaded.AddonList);
        ClusterDefinitions.Remove("dev", dir);
        Assert.Null(ClusterDefinitions.Load("dev", dir));
    }

    [Fact]
    public void A_cluster_Dockyard_did_not_make_gets_a_definition_from_its_nodes()
    {
        var cluster = new KindCluster("ckad", [new("ckad-control-plane", "control-plane", "running"), new("ckad-worker", "worker", "running"), new("ckad-worker2", "worker", "running")], "v1.35.0", 6443);

        var guessed = ClusterDefinitions.Guess(cluster);

        Assert.Equal(KindCli.Config(2), guessed.Yaml);
        Assert.Empty(guessed.Addons);
    }
}
