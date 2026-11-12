using System.Text;

namespace Dockyard.Core;

// /Users is shared with the engine's Linux, so no file under it will do, and nothing outside it is writable without
// root. The keychain file is on that share too, but encrypted with the login password.
sealed class KeychainCaKey(string keychain) : ICaKeyStore
{
    const string Service = "Dockyard Local CA key";
    const string Account = "dockyard";
    const int NotFound = 44;

    public async Task<string?> Read()
    {
        var r = await Shell.Run("/usr/bin/security", "find-generic-password", "-s", Service, "-a", Account, "-w", keychain);
        if (r.ExitCode == NotFound) return null;
        if (r.ExitCode != 0) throw new InvalidOperationException($"The keychain holding the certificate's key could not be read ({r.Error.Trim()}).");
        return Encoding.UTF8.GetString(Convert.FromBase64String(r.Output.Trim()));
    }

    // Over stdin: a secret among a process's arguments can be read by anything that lists processes.
    public async Task Write(string keyPem)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyPem));
        var r = await Shell.RunWithInput("/usr/bin/security", $"add-generic-password -U -s \"{Service}\" -a {Account} -w {encoded} \"{keychain}\"\n", "-i");
        if (r.ExitCode != 0 || await Read() is null) throw new InvalidOperationException($"The certificate's key could not be stored in the keychain ({r.Error.Trim()}).");
    }

    public async Task Delete() => await Shell.Run("/usr/bin/security", "delete-generic-password", "-s", Service, "-a", Account, keychain);
}
