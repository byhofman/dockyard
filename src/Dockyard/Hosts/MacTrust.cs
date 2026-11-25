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
        log("This Mac trusts Dockyard's certificate, for names under dockyard.internal only");
        return true;
    }

    public async Task UntrustCa(Action<string> log, string certificateFile, bool silent)
    {
        if (!File.Exists(certificateFile)) return;
        var thumbprint = LocalCa.Thumbprint(await File.ReadAllTextAsync(certificateFile));
        // Changing trust settings waits for an approval dialog, even as root and without a screen, which hung every headless
        // uninstall. So they go only where a person can answer; silently only the certificate goes, and the setting left
        // behind names a certificate and key that are gone.
        if (silent)
        {
            if (await TrustedIn(admin: true) || await TrustedIn(admin: false))
                log("macOS keeps a trust setting for the removed certificate: removing it needs a person to approve. It vouches for nothing now.");
        }
        else await RemoveTrustSettings(certificateFile);
        // Asking to remove what is not there can still raise an approval dialog, so it is looked up first.
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

    // Trust settings live apart from the certificate, keyed by its hash, so they go first while the file still names them. What
    // is still trusted afterwards, because nobody approved, stops here before anything else goes.
    async Task RemoveTrustSettings(string certificateFile)
    {
        foreach (var admin in new[] { true, false })
            if (await TrustedIn(admin))
                await runner.RunWithin(PromptLimit, "/usr/bin/security", admin ? ["remove-trusted-cert", "-d", certificateFile] : ["remove-trusted-cert", certificateFile]);
        foreach (var admin in new[] { true, false })
            if (await TrustedIn(admin))
                throw new InvalidOperationException($"macOS still trusts Dockyard's certificate in the {(admin ? "administrator's" : "user's")} trust settings, as removing it was not approved. Nothing was removed, so this can be tried again.");
    }

    static readonly TimeSpan PromptLimit = TimeSpan.FromMinutes(2);

    async Task<bool> TrustedIn(bool admin) =>
        (await runner.Run("/usr/bin/security", admin ? ["dump-trust-settings", "-d"] : ["dump-trust-settings"])).Output.Contains(LocalCa.SubjectName, StringComparison.Ordinal);

    async Task<bool> Holds(string keychain, string thumbprint) =>
        (await runner.Run("/usr/bin/security", "find-certificate", "-a", "-Z", "-c", LocalCa.SubjectName, keychain)).Output.Contains(thumbprint, StringComparison.OrdinalIgnoreCase);
}
