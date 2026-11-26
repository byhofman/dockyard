namespace Dockyard.Kind;

// One NFS server for every cluster that asks for it, dockyard-nfs, which the engine's agent keeps running, and in each such
// cluster the NFS CSI driver with the storage class "nfs". A claim's folder is its namespace and name, so clusters share
// files the way the host folder does, but over NFS, as most real clusters do.
static class NfsStorage
{
    public const string ContainerName = Engine.KindNfsServer.ContainerName;
    public const string ClassName = "nfs";

    public static readonly ClusterAddon Driver = new("the NFS CSI driver", "csi-driver-nfs", "csi-driver-nfs",
        "https://raw.githubusercontent.com/kubernetes-csi/csi-driver-nfs/master/charts", "v4.10.0", "kube-system", []);

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

    public static async Task Install(string context, string cluster, string kubeconfig, string? helm, string scratch, Action<string>? onLine)
    {
        if (helm is null) throw new InvalidOperationException("The cluster runs, but NFS storage goes in with Helm, which is not installed.");
        await ClusterAddons.Install(helm, Driver, kubeconfig, cluster, scratch, onLine);
        onLine?.Invoke("Adding the nfs storage class");
        var (exit, output, error) = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, cluster + "-control-plane", StorageClass));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
        if (exit != 0) throw new InvalidOperationException($"Could not add the nfs storage class to {cluster}: {error.Trim()}");
        await SharedStorage.KindDefault(context, cluster, isDefault: false);
    }
}
