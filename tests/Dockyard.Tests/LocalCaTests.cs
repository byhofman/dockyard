using System.Security.Cryptography.X509Certificates;
using Dockyard.Core;

namespace Dockyard.Tests;

public sealed class LocalCaTests : IDisposable
{
    readonly string dir = Directory.CreateTempSubdirectory().FullName;
    static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    static X509Chain ChainTrusting(X509Certificate2 root)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain;
    }

    [Fact]
    public void A_certificate_for_the_domains_validates_against_the_ca()
    {
        var ca = LocalCa.Create(Now);
        var leaf = LocalCa.Leaf(ca, Now);
        using var root = X509Certificate2.CreateFromPem(ca.Certificate);
        using var certificate = X509Certificate2.CreateFromPem(leaf.Certificate, leaf.Key);
        using var chain = ChainTrusting(root);

        Assert.True(chain.Build(certificate), string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation)));
        Assert.True(certificate.MatchesHostname("web.dockyard.test"));
        Assert.False(certificate.MatchesHostname("example.com"));
    }

    [Fact]
    public void The_ca_cannot_vouch_for_any_other_name()
    {
        var ca = LocalCa.Create(Now);
        var rogue = LocalCa.Leaf(ca, Now, ["www.example.com"]);
        using var root = X509Certificate2.CreateFromPem(ca.Certificate);
        using var certificate = X509Certificate2.CreateFromPem(rogue.Certificate, rogue.Key);
        using var chain = ChainTrusting(root);

        Assert.False(chain.Build(certificate));
    }

    [Fact]
    public void The_ca_says_so_itself_and_nobody_can_chain_another_ca_under_it()
    {
        using var root = X509Certificate2.CreateFromPem(LocalCa.Create(Now).Certificate);
        var constraints = root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");
        Assert.True(constraints.Critical);
        Assert.Contains("dockyard.test", System.Text.Encoding.ASCII.GetString(constraints.RawData));
        var basic = root.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(basic.CertificateAuthority);
        Assert.True(basic.HasPathLengthConstraint);
        Assert.Equal(0, basic.PathLengthConstraint);
    }

    [Fact]
    public void A_certificate_for_the_domains_is_short_lived()
    {
        var leaf = LocalCa.Leaf(LocalCa.Create(Now), Now);
        using var certificate = X509Certificate2.CreateFromPem(leaf.Certificate);
        Assert.True(certificate.NotAfter - certificate.NotBefore <= LocalCa.LeafLifetime + TimeSpan.FromDays(2));
    }

    [Fact]
    public void The_key_is_only_readable_by_its_owner_and_is_reused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows goes by the folder's access list");
        var folder = Path.Combine(dir, "ca");
        var first = LocalCa.LoadOrCreate(folder);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(LocalCa.KeyFile(folder)));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Equal(first.Key, LocalCa.LoadOrCreate(folder).Key);

        LocalCa.Remove(folder);
        Assert.False(Directory.Exists(folder));
    }
}
