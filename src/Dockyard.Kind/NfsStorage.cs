namespace Dockyard.Kind;

// One NFS server for every cluster that asks for it: a container on kind's network, serving a volume, and in each such
// cluster the NFS CSI driver with the storage class "nfs". A claim's folder is its namespace and name, so clusters share
// files the way the host folder does, but over NFS, as most real clusters do.
static class NfsStorage
{
    public const string ContainerName = "dockyard-nfs";
    public const string VolumeName = "dockyard-nfs";
    public const string ClassName = "nfs";
    public const string ServiceLabel = "dockyard.service";
    const string Image = "alpine:3.22@sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f";

    public static readonly ClusterAddon Driver = new("the NFS CSI driver", "csi-driver-nfs", "csi-driver-nfs",
        "https://raw.githubusercontent.com/kubernetes-csi/csi-driver-nfs/master/charts", "v4.10.0", "kube-system", []);

    // The kernel's NFS server, version 4 only, so it needs neither rpcbind nor fixed ports. A volume, because the kernel
    // cannot export the container's own overlay filesystem. nfs-utils stays in the container once it is in.
    public const string Script = """
        set -e
        command -v rpc.nfsd >/dev/null || apk add --no-cache nfs-utils >/dev/null
        echo "/export *(rw,fsid=0,sync,no_subtree_check,no_root_squash,insecure)" > /etc/exports
        mountpoint -q /proc/fs/nfsd || mount -t nfsd nfsd /proc/fs/nfsd
        exportfs -ra
        rpc.nfsd -N 3 -V 4 8
        trap 'rpc.nfsd 0; exportfs -ua; exit 0' TERM INT
        rpc.mountd -N 3 -V 4 -F 2>/dev/null &
        wait
        """;

    public static string[] RunArgs(string context) =>
    [
        "--context", context, "run", "-d", "--name", ContainerName, "--restart", "unless-stopped", "--network", "kind",
        "--privileged", "--label", $"{ServiceLabel}=nfs", "-v", $"{VolumeName}:/export", Image, "sh", "-c", Script,
    ];

    public static string StorageClass => $$"""
        apiVersion: storage.k8s.io/v1
        kind: StorageClass
        metadata:
          name: {{ClassName}}
          annotations:
            storageclass.kubernetes.io/is-default-class: "true"
        provisioner: nfs.csi.k8s.io
        parameters:
          server: {{ContainerName}}
          share: /
          subDir: ${pvc.metadata.namespace}/${pvc.metadata.name}
        reclaimPolicy: Retain
        volumeBindingMode: Immediate
        mountOptions:
        - nfsvers=4.2
        """;

    public static async Task EnsureServer(string context, Action<string>? onLine)
    {
        var (exit, output, _) = await SharedStorage.Docker(["--context", context, "inspect", "-f", "{{.State.Running}} {{json .NetworkSettings.Networks}}", ContainerName]);
        if (exit != 0)
        {
            onLine?.Invoke($"Starting the NFS server, {ContainerName}");
            var run = await SharedStorage.Docker(RunArgs(context));
            if (run.ExitCode != 0) throw new InvalidOperationException($"Could not start {ContainerName}: {run.Error.Trim()}");
            return;
        }
        if (!output.Contains("\"kind\"", StringComparison.Ordinal)) await SharedStorage.Docker(["--context", context, "network", "connect", "kind", ContainerName]);
        if (!output.StartsWith("true", StringComparison.Ordinal))
        {
            onLine?.Invoke($"Starting {ContainerName}");
            var start = await SharedStorage.Docker(["--context", context, "start", ContainerName]);
            if (start.ExitCode != 0) throw new InvalidOperationException($"Could not start {ContainerName}: {start.Error.Trim()}");
        }
    }

    public static async Task Install(string context, string cluster, string kubeconfig, string? helm, string scratch, Action<string>? onLine)
    {
        if (helm is null) throw new InvalidOperationException("The cluster runs, but NFS storage goes in with Helm, which is not installed.");
        await EnsureServer(context, onLine);
        await ClusterAddons.Install(helm, Driver, kubeconfig, cluster, scratch, onLine);
        onLine?.Invoke("Adding the nfs storage class");
        var (exit, output, error) = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, cluster + "-control-plane", StorageClass));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
        if (exit != 0) throw new InvalidOperationException($"Could not add the nfs storage class to {cluster}: {error.Trim()}");
        await SharedStorage.KindDefault(context, cluster, isDefault: false);
    }

    // Once no cluster uses it: the files stay in the volume for the next cluster that asks for NFS.
    public static async Task RemoveServerIfUnused(string context, IEnumerable<ClusterDefinition?> remaining)
    {
        if (remaining.Any(d => d?.Storage == ClusterStorage.Nfs)) return;
        await SharedStorage.Docker(["--context", context, "rm", "-f", ContainerName]);
    }
}
