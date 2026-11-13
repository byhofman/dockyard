namespace Dockyard.Core;

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
