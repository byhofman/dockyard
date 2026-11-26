namespace Dockyard.Kind;

// Headlamp, the Kubernetes UI of SIG UI, as an add-on: one plain-HTTP service behind an Ingress for
// dashboard.<cluster>.dockyard.internal, which Dockyard's proxy already routes to the cluster's ingress controller. With
// cert-manager the Ingress gets a certificate from the dockyard ClusterIssuer, trusted on this computer. It signs in with a
// token of the dockyard-dashboard service account, made on request and valid for a day, so none is kept anywhere.
static class Headlamp
{
    public const string Namespace = "headlamp";
    public const string Account = "dockyard-dashboard";

    public static readonly ClusterAddon Addon =
        new("Headlamp", "headlamp", "headlamp", "https://kubernetes-sigs.github.io/headlamp/", "0.46.0", Namespace, []);

    public static string Host(string cluster) => $"dashboard.{cluster}.{ExternalDns.Zone}";

    public static string Manifest(string cluster, bool tls) => $$"""
        apiVersion: v1
        kind: ServiceAccount
        metadata:
          name: {{Account}}
          namespace: {{Namespace}}
        ---
        apiVersion: rbac.authorization.k8s.io/v1
        kind: ClusterRoleBinding
        metadata:
          name: {{Account}}
        roleRef:
          apiGroup: rbac.authorization.k8s.io
          kind: ClusterRole
          name: cluster-admin
        subjects:
        - kind: ServiceAccount
          name: {{Account}}
          namespace: {{Namespace}}
        ---
        apiVersion: networking.k8s.io/v1
        kind: Ingress
        metadata:
          name: headlamp
          namespace: {{Namespace}}
        {{(tls ? $"  annotations:\n    cert-manager.io/cluster-issuer: {CertManager.IssuerName}\n" : "")}}spec:
          ingressClassName: traefik
        {{(tls ? $"  tls:\n  - hosts: [{Host(cluster)}]\n    secretName: headlamp-tls\n" : "")}}  rules:
          - host: {{Host(cluster)}}
            http:
              paths:
              - path: /
                pathType: Prefix
                backend:
                  service:
                    name: headlamp
                    port:
                      number: 80
        """;

    public static async Task Expose(string context, string cluster, bool tls, Action<string>? onLine)
    {
        onLine?.Invoke($"Headlamp at http{(tls ? "s" : "")}://{Host(cluster)}");
        var (exit, output, error) = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, cluster + "-control-plane", Manifest(cluster, tls)));
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
        if (exit != 0) throw new InvalidOperationException($"Headlamp runs, but its address was not set up: {error.Trim()}");
    }

    // A token for signing in, valid for a day.
    public static async Task<string> Token(string context, string cluster)
    {
        var (exit, output, error) = await SharedStorage.Docker(["--context", context, "exec", cluster + "-control-plane",
            "kubectl", "--kubeconfig", SharedStorage.AdminConfig, "-n", Namespace, "create", "token", Account, "--duration", "24h"]);
        if (exit != 0) throw new InvalidOperationException($"No Headlamp token for {cluster}: {error.Trim()}. Is Headlamp installed in it?");
        return output.Trim();
    }
}
