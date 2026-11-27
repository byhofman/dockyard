namespace Dockyard.Cli.Tests;

public class ClusterLineTests
{
    [Fact]
    public void A_cluster_says_its_version_and_how_many_of_its_nodes_run()
    {
        var line = ClusterLine.From("dev", "v1.37.0", "NFS storage", "running\tkindest/node:v1.37.0\nexited\tkindest/node:v1.37.0\n");

        Assert.Equal(new ClusterLine("dev", "v1.37.0", "1 of 2 running", "NFS storage"), line);
        Assert.Equal("2 running", ClusterLine.From("dev", "v1.37.0", null, "running\tx\nrunning\tx").Nodes);
    }

    [Fact]
    public void A_cluster_made_elsewhere_takes_its_version_from_its_nodes()
    {
        var line = ClusterLine.From("other", null, null, "running\tkindest/node:v1.36.1@sha256:abc\n");

        Assert.Equal("v1.36.1", line.Kubernetes);
        Assert.Equal("made elsewhere", line.Details);
    }

    [Fact]
    public void A_cluster_without_node_containers_says_so()
    {
        Assert.Equal(new ClusterLine("gone", "-", "no nodes", "made elsewhere"), ClusterLine.From("gone", null, null, ""));
    }
}
