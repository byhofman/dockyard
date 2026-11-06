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
    public static string KeyFile(string directory) => Path.Combine(directory, "ca.key");

    public static PemPair? Load(string directory) =>
        File.Exists(CertificateFile(directory)) && File.Exists(KeyFile(directory))
            ? new PemPair(File.ReadAllText(CertificateFile(directory)), File.ReadAllText(KeyFile(directory)))
            : null;

    public static PemPair LoadOrCreate(string directory)
    {
        if (Load(directory) is { } existing) return existing;
        var ca = Create(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows()) OwnerOnly(directory);
        else File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Created empty with its mode first, so the key is at no moment readable by anyone else.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(KeyFile(directory), options))
        using (var writer = new StreamWriter(file))
            writer.Write(ca.Key);
        File.WriteAllText(CertificateFile(directory), ca.Certificate);
        return ca;
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

    public static void Remove(string directory)
    {
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
        var ca = LocalCa.LoadOrCreate(host.CaDirectory);
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
        LocalCa.Remove(host.CaDirectory);
        var settings = Settings.Load();
        settings.HttpsEnabled = false;
        settings.Save();
    }

    public static async Task Push(IEngineHost host, PemPair ca, Action<string>? log = null)
    {
        if (AgentClient.For(DockerEndpoint.Parse(host.Engines[0].Endpoint)) is not { } agent) return;
        var leaf = LocalCa.Leaf(ca, DateTimeOffset.UtcNow);
        try { await agent.SetCertificateAsync(leaf.Certificate, leaf.Key); }
        // The engine is stopped. The tray hands the certificate over once it is back.
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException) { log?.Invoke("HTTPS starts once the engine runs and the tray is there"); }
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
            if (!Settings.Load().HttpsEnabled || LocalCa.Load(host.CaDirectory) is not { } ca) continue;
            try
            {
                await agent.DomainsAsync(ct);
                if (!reachable || DateTime.UtcNow - given > TimeSpan.FromDays(1))
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
