using System.Text.RegularExpressions;

namespace Dockyard.Kind;

// cert-manager with a ClusterIssuer called dockyard: a CA from Dockyard.Setup that can only sign for <cluster>.dockyard.internal,
// below Dockyard's root, which this computer already trusts. An Ingress with cert-manager.io/cluster-issuer: dockyard then
// gets a certificate browsers accept, and Dockyard's proxy passes its TLS through untouched.
static partial class CertManager
{
    public const string IssuerName = "dockyard";
    const string SecretName = "dockyard-cluster-ca";

    public static readonly ClusterAddon Addon =
        new("cert-manager", "cert-manager", "cert-manager", "https://charts.jetstack.io", "v1.21.3", "cert-manager", ["crds.enabled=true"]);

    [GeneratedRegex(@"-----BEGIN (?<kind>[A-Z ]+)-----[\s\S]+?-----END \k<kind>-----")]
    private static partial Regex PemBlock();

    // What DockyardSetup.ClusterCa returns: the cluster's CA, its key, the root.
    public static (string Chain, string Key)? Parse(string pem)
    {
        var blocks = PemBlock().Matches(pem.Replace("\r\n", "\n")).Select(m => (Kind: m.Groups["kind"].Value, Text: m.Value)).ToList();
        var certificates = blocks.Where(b => b.Kind == "CERTIFICATE").Select(b => b.Text).ToList();
        var key = blocks.FirstOrDefault(b => b.Kind.EndsWith("PRIVATE KEY", StringComparison.Ordinal)).Text;
        return certificates.Count == 2 && key is not null ? (string.Join('\n', certificates) + "\n", key + "\n") : null;
    }

    static string Indent(string text) => string.Join('\n', text.TrimEnd().Split('\n').Select(l => "    " + l.TrimEnd('\r')));

    public static string IssuerManifest(string chain, string key) => $$"""
        apiVersion: v1
        kind: Secret
        metadata:
          name: {{SecretName}}
          namespace: cert-manager
        type: kubernetes.io/tls
        stringData:
          tls.crt: |
        {{Indent(chain)}}
          tls.key: |
        {{Indent(key)}}
        ---
        apiVersion: cert-manager.io/v1
        kind: ClusterIssuer
        metadata:
          name: {{IssuerName}}
        spec:
          ca:
            secretName: {{SecretName}}
        """;

    // cert-manager's webhook takes a moment after Helm returns, and refuses the ClusterIssuer until it answers.
    public static async Task AddIssuer(string context, string cluster, string pem, Action<string>? onLine)
    {
        if (Parse(pem) is not { } issued) throw new InvalidOperationException("Dockyard gave no certificate authority for the cluster");
        onLine?.Invoke($"Adding the ClusterIssuer {IssuerName}, for names under {cluster}.{ExternalDns.Zone}");
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(3);
        while (true)
        {
            var (exit, output, error) = await SharedStorage.Docker(SharedStorage.ApplyArgs(context, cluster + "-control-plane", IssuerManifest(issued.Chain, issued.Key)));
            if (exit == 0)
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) onLine?.Invoke(line);
                return;
            }
            if (DateTime.UtcNow > until) throw new InvalidOperationException($"cert-manager did not take the ClusterIssuer: {error.Trim()}");
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }
}
