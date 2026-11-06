using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Dockyard.Core;

// Where the CA's private key lives. Read returns null only when there is none; a store that cannot answer right now
// throws, because treating that as "none" would make a new CA and ask for trust all over again.
interface ICaKeyStore
{
    Task<string?> Read();
    Task Write(string keyPem);
    Task Delete();
}

// A Linux host: anything that could read this file past its mode, a privileged container included, is root on the
// machine already, and no store would hold against that.
sealed class FileCaKey(string directory) : ICaKeyStore
{
    string KeyFile => Path.Combine(directory, "ca.key");

    public async Task<string?> Read() => File.Exists(KeyFile) ? await File.ReadAllTextAsync(KeyFile) : null;

    public async Task Write(string keyPem)
    {
        // Created empty with its mode first, so the key is at no moment readable by anyone else.
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var file = new FileStream(KeyFile, options);
        await using var writer = new StreamWriter(file);
        await writer.WriteAsync(keyPem);
    }

    public Task Delete()
    {
        if (File.Exists(KeyFile)) File.Delete(KeyFile);
        return Task.CompletedTask;
    }
}

// C:\ is visible from WSL as /mnt/c, to root in any privileged container. Encrypted for this user, what can be read
// there is of no use without the user's own logon.
[SupportedOSPlatform("windows")]
sealed class DpapiCaKey(string directory) : ICaKeyStore
{
    string KeyFile => Path.Combine(directory, "ca.key.dpapi");

    public async Task<string?> Read()
    {
        if (!File.Exists(KeyFile)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(await File.ReadAllBytesAsync(KeyFile), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException e) { throw new InvalidOperationException($"The certificate's key could not be read ({e.Message})."); }
    }

    public async Task Write(string keyPem)
    {
        try { await File.WriteAllBytesAsync(KeyFile, ProtectedData.Protect(Encoding.UTF8.GetBytes(keyPem), null, DataProtectionScope.CurrentUser)); }
        catch (CryptographicException e) { throw new InvalidOperationException($"The certificate's key could not be stored ({e.Message})."); }
    }

    public Task Delete()
    {
        if (File.Exists(KeyFile)) File.Delete(KeyFile);
        return Task.CompletedTask;
    }
}

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
