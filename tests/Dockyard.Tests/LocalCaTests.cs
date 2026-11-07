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
}
