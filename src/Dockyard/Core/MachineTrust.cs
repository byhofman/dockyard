using System.Security.Cryptography.X509Certificates;

namespace Dockyard.Core;

// Dockyard's local CA in the machine's root store. Runs elevated.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class MachineTrust
{
    public static string EncodeCertificate(string pem) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pem));
    public static string DecodeCertificate(string encoded) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

    public static void TrustCa(Action<string> log, string certificatePem)
    {
        if (!LocalCa.IsConstrainedRoot(certificatePem)) throw new InvalidOperationException("That is not Dockyard's certificate, so it was not trusted");
        using var certificate = X509Certificate2.CreateFromPem(certificatePem);
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        log("Windows trusts Dockyard's certificate, for names under dockyard.test only");
    }

    public static void UntrustCa(Action<string> log)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        var removed = 0;
        foreach (var certificate in store.Certificates.Find(X509FindType.FindBySubjectName, LocalCa.SubjectName, validOnly: false))
        {
            store.Remove(certificate);
            removed++;
        }
        if (removed > 0) log("Dockyard's certificate is out of Windows' trust store");
    }
}
