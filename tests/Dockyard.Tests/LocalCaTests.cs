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
    public async Task The_key_is_only_readable_by_its_owner_and_is_reused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows goes by the folder's access list");
        var folder = Path.Combine(dir, "ca");
        var keys = new FileCaKey(folder);
        var first = await LocalCa.LoadOrCreate(folder, keys);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(folder, "ca.key")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        Assert.Equal(first.Key, (await LocalCa.LoadOrCreate(folder, keys)).Key);

        await LocalCa.Remove(folder, keys);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task On_windows_the_file_holds_no_readable_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI is Windows'");
        var folder = Path.Combine(dir, "ca");
        var keys = new DpapiCaKey(folder);
        var first = await LocalCa.LoadOrCreate(folder, keys);

        Assert.DoesNotContain("PRIVATE KEY", await File.ReadAllTextAsync(Path.Combine(folder, "ca.key.dpapi")));
        Assert.False(File.Exists(Path.Combine(folder, "ca.key")));
        Assert.Equal(first.Key, (await LocalCa.LoadOrCreate(folder, keys)).Key);

        await LocalCa.Remove(folder, keys);
        Assert.Null(await keys.Read());
    }

    [Fact]
    public async Task On_macos_the_key_is_in_the_keychain_and_no_file_holds_it()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The keychain is macOS'");
        var folder = Path.Combine(dir, "ca");
        var keychain = Path.Combine(dir, "test.keychain");
        await Shell.Check("/usr/bin/security", "create-keychain", "-p", "test", keychain);
        await Shell.Check("/usr/bin/security", "unlock-keychain", "-p", "test", keychain);
        try
        {
            var keys = new KeychainCaKey(keychain);
            Assert.Null(await keys.Read());
            var first = await LocalCa.LoadOrCreate(folder, keys);

            Assert.False(File.Exists(Path.Combine(folder, "ca.key")));
            Assert.Equal(first.Key, (await LocalCa.LoadOrCreate(folder, keys)).Key);

            await LocalCa.Remove(folder, keys);
            Assert.Null(await keys.Read());
        }
        finally { await Shell.Run("/usr/bin/security", "delete-keychain", keychain); }
    }

    [Fact]
    public void An_elevated_step_only_trusts_a_root_with_the_constraint()
    {
        Assert.True(LocalCa.IsConstrainedRoot(LocalCa.Create(Now).Certificate));

        using var key = System.Security.Cryptography.ECDsa.Create();
        var other = new CertificateRequest($"CN={LocalCa.SubjectName}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        other.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var unconstrained = other.CreateSelfSigned(Now, Now.AddDays(1));
        Assert.False(LocalCa.IsConstrainedRoot(unconstrained.ExportCertificatePem()));
        Assert.False(LocalCa.IsConstrainedRoot("not a certificate"));
    }

    [Fact]
    public void A_certificate_for_an_IP_address_does_not_validate_against_the_ca()
    {
        var ca = LocalCa.Create(Now);
        using var root = X509Certificate2.CreateFromPem(ca.Certificate, ca.Key);
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=10.0.0.1", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddIpAddress(System.Net.IPAddress.Parse("10.0.0.1"));
        request.CertificateExtensions.Add(san.Build(critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        using var issued = request.Create(root, Now.AddDays(-1), Now.AddDays(30), [1, 2, 3, 4]);
        using var publicRoot = X509Certificate2.CreateFromPem(ca.Certificate);
        using var chain = ChainTrusting(publicRoot);

        Assert.False(chain.Build(issued));
    }

    [Fact]
    public void A_root_from_before_the_IP_and_usage_limits_is_not_trusted_any_more()
    {
        using var key = System.Security.Cryptography.ECDsa.Create();
        var old = new CertificateRequest($"CN={LocalCa.SubjectName}", key, System.Security.Cryptography.HashAlgorithmName.SHA256);
        old.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        // The constraint as earlier versions wrote it: dockyard.test permitted, nothing excluded, no extended key usage.
        old.CertificateExtensions.Add(new X509Extension("2.5.29.30", Convert.FromHexString("3013A011300F820D646F636B796172642E74657374"), critical: true));
        using var earlier = old.CreateSelfSigned(Now, Now.AddDays(1));

        Assert.False(LocalCa.IsConstrainedRoot(earlier.ExportCertificatePem()));
    }
}
