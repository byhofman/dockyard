namespace Dockyard.Engine;

// One NFS server for every cluster that stores on NFS: a container on kind's network, serving a volume.
public static class KindNfsServer
{
    public const string ContainerName = "dockyard-nfs";
    public const string VolumeName = "dockyard-nfs";
    const string Image = "alpine:3.22@sha256:49340b346798875b5592e5d420241f2f9cfba5f47e62ebb1446a1b34734e898f";

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

    public static ServiceContainerSpec Spec { get; } = new(ContainerName, Image, ["sh", "-c", Script.Replace("\r\n", "\n")],
        new Dictionary<string, string> { [KindCloudProvider.ServiceLabel] = "nfs" }, [$"{VolumeName}:/export"], [], Privileged: true);
}
