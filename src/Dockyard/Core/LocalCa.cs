using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Hosts;

namespace Dockyard.Core;

sealed record PemPair(string Certificate, string Key);

// A certificate authority that exists on this device only and can only ever vouch for one name space, so even a stolen
// key could not impersonate any real site. Its key never leaves the host: the agent next to the engine, which any
// privileged container could read, only gets a short-lived certificate for the domains themselves.
static class LocalCa
{
    public const string Zone = "dockyard.test";
    public const string SubjectName = "Dockyard Local CA";
    const string NameConstraintsOid = "2.5.29.30";
    static readonly TimeSpan CaLifetime = TimeSpan.FromDays(3650);
    public static readonly TimeSpan LeafLifetime = TimeSpan.FromDays(90);

    public static PemPair Create(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={SubjectName}, O=Dockyard on this device", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509Extension(NameConstraintsOid, PermittedDnsName(Zone), critical: true));
        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now + CaLifetime);
        return new PemPair(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    // NameConstraints ::= SEQUENCE { permittedSubtrees [0] { SEQUENCE { base [2] dNSName } } }
    static byte[] PermittedDnsName(string name)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        using (writer.PushSequence())
            writer.WriteCharacterString(UniversalTagNumber.IA5String, name, new Asn1Tag(TagClass.ContextSpecific, 2));
        return writer.Encode();
    }

    public static PemPair Leaf(PemPair ca, DateTimeOffset now, string[]? names = null)
    {
        using var authority = X509Certificate2.CreateFromPem(ca.Certificate, ca.Key);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=*.{Zone}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names ?? [$"*.{Zone}", Zone]) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build(critical: true));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, includeKeyIdentifier: true, includeIssuerAndSerial: false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var issued = request.Create(authority, now.AddDays(-1), now + LeafLifetime, serial);
        return new PemPair(issued.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());
    }

    public static string CertificateFile(string directory) => Path.Combine(directory, "ca.pem");

    public static async Task<PemPair?> Load(string directory, ICaKeyStore keys) =>
        File.Exists(CertificateFile(directory)) && await keys.Read() is { } key
            ? new PemPair(await File.ReadAllTextAsync(CertificateFile(directory)), key)
            : null;

    public static async Task<PemPair> LoadOrCreate(string directory, ICaKeyStore keys)
    {
        if (await Load(directory, keys) is { } existing) return existing;
        var ca = Create(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) OwnerOnly(directory);
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await keys.Write(ca.Key);
        await File.WriteAllTextAsync(CertificateFile(directory), ca.Certificate);
        return ca;
    }

    // What an elevated step adds to the machine's trust store comes in from a less trusted process, so it must be what
    // this code would have made: a root that can only vouch for the one name space.
    public static bool IsConstrainedRoot(string certificatePem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(certificatePem);
            return certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == SubjectName
                && certificate.Extensions[NameConstraintsOid] is { Critical: true } constraint
                && constraint.RawData.AsSpan().SequenceEqual(PermittedDnsName(Zone));
        }
        catch (CryptographicException) { return false; }
    }

    [SupportedOSPlatform("windows")]
    static void OwnerOnly(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    public static async Task Remove(string directory, ICaKeyStore keys)
    {
        await keys.Delete();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    public static string Thumbprint(string certificatePem)
    {
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        return certificate.Thumbprint;
    }
}

// Trusted HTTPS on or off. Off is the default and everything else works without it, over plain HTTP.
static class Https
{
    public static async Task Enable(IEngineHost host, Action<string> log, bool silent)
    {
        PemPair ca;
        try { ca = await LocalCa.LoadOrCreate(host.CaDirectory, host.CaKeys); }
        catch (InvalidOperationException e)
        {
            log($"HTTPS was not turned on: {e.Message} Plain HTTP works as before.");
            return;
        }
        if (!await host.TrustCa(log, LocalCa.CertificateFile(host.CaDirectory), silent))
            log("The certificate was not added to this computer's trust store, so browsers warn about HTTPS until it is. Plain HTTP works as before.");
        var settings = Settings.Load();
        settings.HttpsEnabled = true;
        settings.Save();
        await Push(host, ca, log);
    }

    public static async Task Disable(IEngineHost host, Action<string> log)
    {
        await host.UntrustCa(log, LocalCa.CertificateFile(host.CaDirectory));
        await LocalCa.Remove(host.CaDirectory, host.CaKeys);
        var settings = Settings.Load();
        settings.HttpsEnabled = false;
        settings.Save();
    }

    public static async Task Push(IEngineHost host, PemPair ca, Action<string>? log = null)
    {
        if (AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint)) is not { } agent) return;
        var leaf = LocalCa.Leaf(ca, DateTimeOffset.UtcNow);
        // Setup has just restarted the agent when the engine runs, and it sets up its rules before it answers.
        var attempts = await host.IsRunning(host.Engines[0].Id) ? 15 : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await agent.SetCertificateAsync(leaf.Certificate, leaf.Key);
                return;
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
            {
                if (attempt < attempts) { await Task.Delay(TimeSpan.FromSeconds(1)); continue; }
                // The tray hands the certificate over once the engine is back.
                log?.Invoke("HTTPS starts once the engine runs and the tray is there");
                return;
            }
        }
    }

    // The agent keeps its certificate in memory only, so it has none after each engine start, and one lasts 90 days.
    public static async Task KeepCertificateCurrent(IEngineHost host, CancellationToken ct)
    {
        if (AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint)) is not { } agent) return;
        var given = DateTime.MinValue;
        var reachable = false;
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            if (!Settings.Load().HttpsEnabled) continue;
            try
            {
                await agent.DomainsAsync(ct);
                // Only when a certificate is due: the key can be in the OS's store, which is not for asking every 10 seconds.
                if ((!reachable || DateTime.UtcNow - given > TimeSpan.FromDays(1)) && await LocalCa.Load(host.CaDirectory, host.CaKeys) is { } ca)
                {
                    var leaf = LocalCa.Leaf(ca, DateTimeOffset.UtcNow);
                    await agent.SetCertificateAsync(leaf.Certificate, leaf.Key, ct);
                    given = DateTime.UtcNow;
                }
                reachable = true;
            }
            catch (Exception e) when (e is not OperationCanceledException) { reachable = false; }
        }
    }
}
