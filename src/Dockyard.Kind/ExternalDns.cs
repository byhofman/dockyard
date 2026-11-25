namespace Dockyard.Kind;

// external-dns in a cluster with the load balancer: Ingress hosts and annotated LoadBalancer services under
// dockyard.internal go to dockyard-etcd, under a prefix of the cluster's own, and Dockyard's DNS answers them everywhere.
// A prefix per cluster lets each one's sync remove only what it published itself.
static class ExternalDns
{
    public const string Namespace = "external-dns";
    public const string Image = "registry.k8s.io/external-dns/external-dns:v0.20.0@sha256:ddc7f4212ed09a21024deb1f470a05240837712e74e4b9f6d1f2632ff10672e7";
    public const string Zone = "dockyard.internal";
    // dockyard-etcd on the engine's own address, which kind's nodes reach through their gateway.
    public const string EtcdUrl = "http://198.19.250.1:2379";

    public static string Manifest(string cluster) => $$"""
        apiVersion: v1
        kind: Namespace
        metadata:
          name: {{Namespace}}
        ---
        apiVersion: v1
        kind: ServiceAccount
        metadata:
          name: external-dns
          namespace: {{Namespace}}
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRole
        metadata:
          name: external-dns
        rules:
        - apiGroups: [""]
          resources: [services, endpoints, pods, nodes]
          verbs: [get, watch, list]
        - apiGroups: [discovery.k8s.io]
          resources: [endpointslices]
          verbs: [get, watch, list]
        - apiGroups: [networking.k8s.io]
          resources: [ingresses]
          verbs: [get, watch, list]
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRoleBinding
        metadata:
          name: external-dns
        roleRef:
          apiGroup: rbac.authorization.k8s.io
          kind: ClusterRole
          name: external-dns
        subjects:
        - kind: ServiceAccount
          name: external-dns
          namespace: {{Namespace}}
        ---
        apiVersion: apps/v1
        kind: Deployment
        metadata:
          name: external-dns
          namespace: {{Namespace}}
        spec:
          replicas: 1
          strategy:
            type: Recreate
          selector:
            matchLabels:
              app: external-dns
          template:
            metadata:
              labels:
                app: external-dns
            spec:
              serviceAccountName: external-dns
              containers:
              - name: external-dns
                image: {{Image}}
                args:
                - --source=ingress
                - --source=service
                - --provider=coredns
                - --coredns-prefix=/dockyard/{{cluster}}/
                - --domain-filter={{Zone}}
                - --registry=noop
                - --policy=sync
                - --interval=10s
                env:
                - name: ETCD_URLS
                  value: {{EtcdUrl}}
        """;

    // What a deleted cluster published goes with it; dockyard-etcd may not run, which leaves nothing to clean.
    public static Task Forget(string context, string cluster) =>
        SharedStorage.Docker(["--context", context, "exec", "dockyard-etcd", "etcdctl", "--endpoints", EtcdUrl, "del", "--prefix", $"/dockyard/{cluster}/"]);

    public static async Task Install(string context, string cluster, Action<string>? onLine)
    {
        onLine?.Invoke($"Adding external-dns: Ingress hosts under {Zone} resolve everywhere");
        var (exit, output, error) = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, cluster + "-control-plane", Manifest(cluster)));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
        if (exit != 0) throw new InvalidOperationException($"Could not add external-dns to {cluster}: {error.Trim()}");
    }
}
