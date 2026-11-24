using Dockyard.Dashboard.Features.Clusters;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Tests;

public sealed class SharedStorageTests
{
    [Fact]
    public void Every_node_of_a_cluster_with_shared_storage_mounts_the_engines_folder()
    {
        var mount = "  extraMounts:\n  - hostPath: /var/lib/dockyard/shared\n    containerPath: /var/dockyard-shared\n";
        Assert.Equal("kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: control-plane\n" + mount + "- role: worker\n" + mount, KindCli.Config(1, sharedStorage: true));
        Assert.Equal("kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: control-plane\n" + mount, KindCli.Config(0, sharedStorage: true));
    }

    [Fact]
    public void The_provisioner_uses_the_images_kind_already_has_and_keeps_files_by_namespace_and_name()
    {
        var output = "docker.io/kindest/local-path-provisioner:v20260820-69b56db7 [\"local-path-provisioner\",\"--debug\",\"start\",\"--helper-image\",\"docker.io/kindest/local-path-helper:v20260131-7181c60a\",\"--config\",\"/etc/config/config.json\"]";
        var images = SharedStorage.Images(output);
        Assert.Equal(("docker.io/kindest/local-path-provisioner:v20260820-69b56db7", "docker.io/kindest/local-path-helper:v20260131-7181c60a"), images);
        Assert.Null(SharedStorage.Images("Error from server (NotFound)"));

        var manifest = SharedStorage.Manifest(images!.Value.Provisioner, images.Value.Helper);
        Assert.Contains("image: docker.io/kindest/local-path-provisioner:v20260820-69b56db7", manifest);
        Assert.Contains("{\"sharedFileSystemPath\": \"/var/dockyard-shared\"}", manifest);
        Assert.Contains("pathPattern: \"{{ .PVC.Namespace }}/{{ .PVC.Name }}/files\"", manifest);
        Assert.Contains("reclaimPolicy: Retain", manifest);
        Assert.Contains("  name: shared\n", manifest);
    }

    [Fact]
    public void The_manifest_goes_to_the_shell_as_an_argument_never_inside_its_script()
    {
        var args = SharedStorage.ApplyArgs("linux", "dev-control-plane", "x'; rm -rf / #");
        Assert.Equal(["--context", "linux", "exec", "dev-control-plane", "sh", "-c"], args[..6]);
        Assert.DoesNotContain("rm -rf", args[6]);
        Assert.Equal(["sh", "x'; rm -rf / #"], args[7..]);
    }

    static ContainerItem Node(string cluster, string role, params string[] binds)
    {
        var item = new ContainerItem(cluster + role);
        item.Update(new ContainerSummary
        {
            Id = cluster + role,
            Names = [$"/{cluster}-{role}"],
            Image = "kindest/node:v1.37.0",
            State = "running",
            Labels = new() { ["io.x-k8s.kind.cluster"] = cluster, ["io.x-k8s.kind.role"] = role },
            Mounts = binds.Select(b => new MountPoint { Type = "bind", Source = "/var/lib/dockyard/shared", Destination = b }).ToList(),
        });
        return item;
    }

    [Fact]
    public void A_cluster_has_shared_storage_only_when_every_node_mounts_the_folder()
    {
        var clusters = KindClusters.From([
            Node("a", "control-plane", "/var/dockyard-shared"), Node("a", "worker", "/var/dockyard-shared"),
            Node("b", "control-plane", "/var/dockyard-shared"), Node("b", "worker"),
            Node("c", "control-plane"),
        ]);
        Assert.Equal([true, false, false], clusters.Select(c => c.HasSharedStorage));
    }
}
