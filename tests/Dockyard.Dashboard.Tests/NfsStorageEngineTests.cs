using Dockyard.Dashboard.Features.Clusters;

namespace Dockyard.Dashboard.Tests;

// A real two-node cluster with NFS storage on the engine the environment names: pods on both nodes write to one claim
// of the class "nfs", and the files are in the dockyard-nfs volume.
public sealed class NfsStorageEngineTests
{
    [Fact]
    public async Task Pods_on_both_nodes_write_to_one_nfs_claim_and_the_files_are_in_the_servers_volume()
    {
        if (Environment.GetEnvironmentVariable("DOCKYARD_TEST_KIND") is not { Length: > 0 } kind) { Assert.Skip("No kind named"); return; }
        if (Environment.GetEnvironmentVariable("DOCKYARD_TEST_HELM") is not { Length: > 0 } helm) { Assert.Skip("No helm named"); return; }
        var context = Environment.GetEnvironmentVariable("DOCKYARD_TEST_DOCKER_CONTEXT") is { Length: > 0 } c ? c : "default";
        var dir = Directory.CreateTempSubdirectory("dockyard-nfs-engine-test-").FullName;
        var actions = new KindActions(kind, context, new HostKubeconfig([Path.Combine(dir, "config")]));
        const string name = "dockyard-nfs-e2e";
        var controlPlane = name + "-control-plane";
        var ns = "dockyard-e2e-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await actions.Create(name, workers: 1, storage: ClusterStorage.Nfs, helm: helm);
            var helper = SharedStorage.Images((await SharedStorage.Docker(SharedStorage.ImagesArgs(context, controlPlane))).Output)!.Value.Helper;
            var manifest = string.Join("\n---\n",
                $"apiVersion: v1\nkind: Namespace\nmetadata:\n  name: {ns}",
                $"apiVersion: v1\nkind: PersistentVolumeClaim\nmetadata:\n  name: data\n  namespace: {ns}\nspec:\n  accessModes: [ReadWriteMany]\n  storageClassName: {NfsStorage.ClassName}\n  resources:\n    requests:\n      storage: 1Gi",
                Pod("on-control-plane", controlPlane, ns, helper),
                Pod("on-worker", name + "-worker", ns, helper));
            var applied = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, controlPlane, manifest));
            Assert.True(applied.ExitCode == 0, applied.Error);
            var wait = await SharedStorage.Docker(["--context", context, "exec", controlPlane, "kubectl", "--kubeconfig", "/etc/kubernetes/admin.conf",
                "-n", ns, "wait", "--for=condition=Ready", "pod/on-control-plane", "pod/on-worker", "--timeout=300s"]);
            Assert.True(wait.ExitCode == 0, wait.Error + wait.Output);
            var files = (ExitCode: 1, Output: "", Error: "");
            for (var i = 0; i < 30 && files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < 2; i++)
            {
                await Task.Delay(1000);
                files = await SharedStorage.Docker(["--context", context, "exec", NfsStorage.ContainerName, "ls", $"/export/{ns}/data"]);
            }
            Assert.Equal(["on-control-plane.txt", "on-worker.txt"], files.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order());
        }
        finally
        {
            try { await actions.Delete(name); } catch (Exception) { }
            await SharedStorage.Docker(["--context", context, "exec", NfsStorage.ContainerName, "rm", "-rf", "/export/" + ns]);
            Directory.Delete(dir, recursive: true);
        }
    }

    static string Pod(string pod, string node, string ns, string image) =>
        $"apiVersion: v1\nkind: Pod\nmetadata:\n  name: {pod}\n  namespace: {ns}\nspec:\n  nodeName: {node}\n  tolerations: [{{operator: Exists}}]\n" +
        $"  containers:\n  - name: c\n    image: \"{image}\"\n    imagePullPolicy: IfNotPresent\n    command: [sh, -c, \"echo {pod} > /data/{pod}.txt; sleep 600\"]\n" +
        "    volumeMounts: [{name: d, mountPath: /data}]\n  volumes: [{name: d, persistentVolumeClaim: {claimName: data}}]";
}
