using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Dockyard.Core;

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
