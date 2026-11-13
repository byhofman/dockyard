using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Dockyard's local CA in the system's trust store and in this user's browser store.
[SupportedOSPlatform("linux")]
sealed class LinuxTrust(LinuxRoot root)
{
    static string NssDb => Path.Combine(Paths.UserProfile, ".pki", "nssdb");

    public async Task<bool> TrustCa(Action<string> log, string certificateFile)
    {
        try
        {
            // A browser run by this user reads its own store on Linux, where it exists; the system's covers curl and the rest.
            await root.RunSetup(log, "trust-ca", "", certificateFile);
            var nss = NssDb;
            if (Environment.UserName != "root" && Directory.Exists(nss))
                await root.TryRun("certutil", "-d", "sql:" + nss, "-A", "-t", "C,,", "-n", LocalCa.SubjectName, "-i", certificateFile);
            return true;
        }
        catch (InvalidOperationException e)
        {
            log(e.Message);
            return false;
        }
    }

    public async Task UntrustCa(Action<string> log, string certificateFile)
    {
        if (!File.Exists(certificateFile)) return;
        var nss = NssDb;
        if (Directory.Exists(nss)) await root.TryRun("certutil", "-d", "sql:" + nss, "-D", "-n", LocalCa.SubjectName);
        await root.RunSetup(log, "untrust-ca");
    }
}
