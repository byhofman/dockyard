using Dockyard.Kind;

namespace Dockyard.Dashboard.Tests;

public sealed class CertManagerTests
{
    const string ClusterCa = """
        -----BEGIN CERTIFICATE-----
        AAAA
        -----END CERTIFICATE-----
        -----BEGIN PRIVATE KEY-----
        BBBB
        -----END PRIVATE KEY-----
        -----BEGIN CERTIFICATE-----
        CCCC
        -----END CERTIFICATE-----
        """;

    [Fact]
    public void The_cluster_ca_is_the_chain_and_the_key()
    {
        var (chain, key) = CertManager.Parse(ClusterCa)!.Value;

        Assert.StartsWith("-----BEGIN CERTIFICATE-----\nAAAA", chain);
        Assert.Contains("CCCC", chain);
        Assert.DoesNotContain("BBBB", chain);
        Assert.Contains("BBBB", key);
        Assert.Null(CertManager.Parse("Trusted HTTPS is off"));
    }

    [Fact]
    public void The_issuer_takes_the_chain_from_a_tls_secret()
    {
        var (chain, key) = CertManager.Parse(ClusterCa)!.Value;
        var manifest = CertManager.IssuerManifest(chain, key).Replace("\r\n", "\n");

        Assert.Contains("kind: ClusterIssuer\nmetadata:\n  name: dockyard\nspec:\n  ca:\n    secretName: dockyard-cluster-ca", manifest);
        Assert.Contains("  tls.crt: |\n    -----BEGIN CERTIFICATE-----\n    AAAA\n", manifest);
        Assert.Contains("  tls.key: |\n    -----BEGIN PRIVATE KEY-----\n    BBBB\n", manifest);
        Assert.Contains("cert-manager", ClusterAddons.All.Select(a => a.Release));
    }
}
