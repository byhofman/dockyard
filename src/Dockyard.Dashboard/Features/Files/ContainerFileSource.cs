using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

// A container's files, or a volume's through a holder that never runs. A running container answers one folder at a time
// through its shell; otherwise the archive of the whole tree is read once and every folder after that comes from memory,
// until Refresh or a change made here.
sealed class ContainerFileSource(DockerClient client, string container, string root, bool canWrite, Func<Task>? release = null,
    Func<string, CancellationToken, Task<IReadOnlyList<FileEntry>?>>? listFolder = null) : IFileSource
{
    Task<FileTree>? tree;
    bool folderListingFailed;

    public string Root => root;
    public bool CanWrite => canWrite;

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string directory, IProgress<string> status, CancellationToken ct)
    {
        if (listFolder is not null && !folderListingFailed)
        {
            if (await listFolder(directory, ct) is { } entries) return entries;
            folderListingFailed = true;
        }
        tree ??= Read(status, ct);
        var read = await tree;
        return read.List(directory) ?? throw new DirectoryNotFoundException(directory);
    }

    async Task<FileTree> Read(IProgress<string> status, CancellationToken ct)
    {
        try
        {
            status.Report("Reading the files…");
            using var response = await client.GetArchiveAsync(container, root, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await FileTree.ReadAsync(stream, new Progress<long>(n => status.Report($"Reading the files: {Format.Bytes(n)}")), ct);
        }
        catch
        {
            tree = null;
            throw;
        }
    }

    public void Forget()
    {
        tree = null;
        folderListingFailed = false;
    }

    public Task<FileContent> ReadAsync(string path, int maxBytes, CancellationToken ct) =>
        ContainerFiles.ReadAsync(client, container, path, maxBytes, ct);

    public Task SaveToAsync(string path, Stream destination, CancellationToken ct) =>
        ContainerFiles.SaveToAsync(client, container, path, destination, ct);

    public async Task WriteAsync(string directory, string name, Stream content, FileEntry? like, CancellationToken ct)
    {
        if (!canWrite) throw new InvalidOperationException("These files are read-only.");
        await ContainerFiles.WriteAsync(client, container, directory, name, content, like, ct);
        tree = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (release is not null) await release();
    }
}
