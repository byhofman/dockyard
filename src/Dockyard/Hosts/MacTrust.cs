using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Dockyard's local CA in this Mac's keychains.
[SupportedOSPlatform("macos")]
sealed class MacTrust(ICommandRunner runner)
{
    const string SystemKeychain = "/Library/Keychains/System.keychain";

    // A window: the login keychain, for which macOS asks the user's own approval. Silent: only what sudo allows unasked.
    public async Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent)
    {
        var r = silent
            ? await runner.Run("sudo", "-n", "/usr/bin/security", "add-trusted-cert", "-d", "-r", "trustRoot", "-p", "ssl", "-k", SystemKeychain, certificateFile)
            : await runner.Run("/usr/bin/security", "add-trusted-cert", "-r", "trustRoot", "-p", "ssl", "-k", MacLayout.LoginKeychain, certificateFile);
        if (r.ExitCode != 0) return false;
        log("This Mac trusts Dockyard's certificate, for names under dockyard.test only");
        return true;
    }

    public async Task UntrustCa(Action<string> log, string certificateFile)
    {
        if (!File.Exists(certificateFile)) return;
        var thumbprint = LocalCa.Thumbprint(await File.ReadAllTextAsync(certificateFile));
        // Asking to remove what is not there can still raise an approval dialog, so it is looked up first. Only the certificate
        // goes: remove-trusted-cert waits for an approval dialog, even as root and without a screen, so it hung every headless
        // uninstall. The trust setting left behind is for this certificate's hash alone, and it and its key are gone.
        if (await Holds(MacLayout.LoginKeychain, thumbprint))
        {
            await runner.Run("/usr/bin/security", "delete-certificate", "-Z", thumbprint, MacLayout.LoginKeychain);
        }
        // In the system keychain only after a silent setup; a prompt for what is not there would make no sense.
        if (await Holds(SystemKeychain, thumbprint))
        {
            var script = $"security delete-certificate -Z {thumbprint} {SystemKeychain}";
            if ((await runner.Run("sudo", "-n", "/bin/sh", "-c", script)).ExitCode != 0)
                await runner.Run("/usr/bin/osascript", MacLayout.AdminShell(script));
        }
        foreach (var keychain in new[] { MacLayout.LoginKeychain, SystemKeychain })
            if (await Holds(keychain, thumbprint))
                throw new InvalidOperationException($"Dockyard's certificate is still in {keychain}. Nothing was removed, so this can be tried again.");
        log("Dockyard's certificate is out of this Mac's trust store");
    }

    async Task<bool> Holds(string keychain, string thumbprint) =>
        (await runner.Run("/usr/bin/security", "find-certificate", "-a", "-Z", "-c", LocalCa.SubjectName, keychain)).Output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
}
