using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class ClusterLogicTests
{
    static ContainerItem Node(string cluster, string role, string state = "running", string image = "kindest/node:v1.37.0@sha256:a1ed56", int? apiPort = null)
    {
        var name = $"{cluster}-{role}";
        var item = new ContainerItem(name + "-id");
        item.Update(new ContainerSummary
        {
            Id = name + "-id", Names = ["/" + name], State = state, Image = image,
            Labels = new() { [KindClusters.ClusterLabel] = cluster, [KindClusters.RoleLabel] = role },
            Ports = apiPort is { } port ? [new PortMapping { IP = "127.0.0.1", PrivatePort = 6443, PublicPort = port, Type = "tcp" }] : [],
        });
        return item;
    }

    static ContainerItem Plain(string name)
    {
        var item = new ContainerItem(name);
        item.Update(new ContainerSummary { Id = name, Names = ["/" + name], State = "running", Image = "nginx:alpine" });
        return item;
    }

    [Fact]
    public void Clusters_come_from_kind_s_labels_with_their_nodes_version_and_api_server()
    {
        var clusters = KindClusters.From([Plain("web"), Node("dev", "control-plane", apiPort: 52731), Node("dev", "worker"), Node("ci", "control-plane", apiPort: 52800)]);

        Assert.Equal(["ci", "dev"], clusters.Select(c => c.Name));
        var dev = clusters[1];
        Assert.Equal(["dev-control-plane", "dev-worker"], dev.Nodes.Select(n => n.Name));
        Assert.Equal("v1.37.0", dev.KubernetesVersion);
        Assert.Equal("https://127.0.0.1:52731", dev.ApiServer);
        Assert.Equal("kind-dev", dev.Context);
        Assert.True(dev.IsRunning);
        Assert.Equal("2 nodes", dev.NodesText);
    }

    [Fact]
    public void With_several_control_planes_the_api_server_is_the_load_balancer_s_port()
    {
        var clusters = KindClusters.From([
            Node("ha", "control-plane", apiPort: 40001), Node("ha", "control-plane2", apiPort: 40002), Node("ha", "external-load-balancer", image: "docker.io/kindest/haproxy:v2", apiPort: 40000),
        ]);
        Assert.Equal("https://127.0.0.1:40000", clusters.Single().ApiServer);
        Assert.Equal("v1.37.0", clusters.Single().KubernetesVersion);
    }

    [Fact]
    public void A_stopped_node_makes_the_cluster_not_running_and_without_an_api_server()
    {
        var cluster = KindClusters.From([Node("dev", "control-plane", state: "exited"), Node("dev", "worker")]).Single();
        Assert.False(cluster.IsRunning);
        Assert.Equal("Partly stopped", cluster.StateText);
        Assert.Null(cluster.ApiServer);

        var stopped = KindClusters.From([Node("dev", "control-plane", state: "exited")]).Single();
        Assert.Equal("Stopped", stopped.StateText);
        Assert.Equal("1 node", stopped.NodesText);
    }

    [Fact]
    public void An_image_without_a_tag_gives_no_version_instead_of_a_wrong_one() =>
        Assert.Null(KindClusters.From([Node("dev", "control-plane", image: "sha256:0123456789ab")]).Single().KubernetesVersion);

    [Theory]
    [InlineData("dev", true)]
    [InlineData("team-a.v2", true)]
    [InlineData("Dev", false)]
    [InlineData("my cluster", false)]
    [InlineData("", false)]
    [InlineData("a_b", false)]
    public void Names_follow_what_kind_accepts(string name, bool valid) => Assert.Equal(valid, KindClusters.IsValidName(name));

    [Fact]
    public void Kind_runs_against_the_dashboard_s_context_with_the_docker_cli_it_found()
    {
        var sep = Path.PathSeparator;
        var bin = Path.Combine(Path.GetTempPath(), ".dockyard", "bin");
        var env = KindCli.Environment("linux", Path.Combine(bin, "docker"), $"/usr/bin{sep}/bin");
        Assert.Equal("linux", env["DOCKER_CONTEXT"]);
        Assert.Equal("docker", env["KIND_EXPERIMENTAL_PROVIDER"]);
        Assert.Equal($"{bin}{sep}/usr/bin{sep}/bin", env["PATH"]);

        Assert.Equal($"/usr/bin{sep}/bin", KindCli.Environment("default", "docker", $"/usr/bin{sep}/bin")["PATH"]);
    }

    [Fact]
    public void Kind_is_Dockyard_s_own_first_then_one_on_the_path()
    {
        var own = KindCli.OwnPath("/home/me");
        Assert.Equal(own, KindCli.Find(f => f == own || f.EndsWith("other/kind", StringComparison.Ordinal) || f.EndsWith("other\\kind.exe", StringComparison.Ordinal), "other", "/home/me"));
        var onPath = Path.Combine("other", OperatingSystem.IsWindows() ? "kind.exe" : "kind");
        Assert.Equal(onPath, KindCli.Find(f => f == onPath, "other", "/home/me"));
        Assert.Null(KindCli.Find(_ => false, "other", "/home/me"));
    }

    [Fact]
    public void Commands_name_the_cluster()
    {
        Assert.Equal(["create", "cluster", "--name", "dev"], KindCli.Create("dev"));
        Assert.Equal(["delete", "cluster", "--name", "dev"], KindCli.Delete("dev"));
        Assert.Equal(["get", "kubeconfig", "--name", "dev"], KindCli.Kubeconfig("dev"));
        Assert.Equal(["export", "kubeconfig", "--name", "dev"], KindCli.UseContext("dev"));
    }
}
