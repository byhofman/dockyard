using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

// An image's files through a container of it that is never started. The whole tree is read once, on the first listing,
// and every folder after that comes from memory.
sealed class ImageFileSource(DockerClient client, string holder, long imageSize) : IFileSource
{
    Task<FileTree>? tree;

    public string Root => "/";
    public bool CanWrite => false;

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string directory, IProgress<string> status, CancellationToken ct)
    {
        tree ??= Read(status, ct);
        var read = await tree;
        return read.List(directory) ?? throw new DirectoryNotFoundException(directory);
    }

    async Task<FileTree> Read(IProgress<string> status, CancellationToken ct)
    {
        try
        {
            status.Report(Reading(0));
            using var response = await client.GetArchiveAsync(holder, "/", ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await FileTree.ReadAsync(stream, new Progress<long>(n => status.Report(Reading(n))), ct);
        }
        catch
        {
            tree = null;
            throw;
        }
    }

    string Reading(long bytes) => imageSize > 0
        ? $"Reading the image's files: {Format.Bytes(bytes)} of about {Format.Bytes(imageSize)}"
        : $"Reading the image's files: {Format.Bytes(bytes)}";

    public Task<FileContent> ReadAsync(string path, int maxBytes, CancellationToken ct) =>
        ContainerFiles.ReadAsync(client, holder, path, maxBytes, ct);

    public Task SaveToAsync(string path, Stream destination, CancellationToken ct) =>
        ContainerFiles.SaveToAsync(client, holder, path, destination, ct);

    public Task WriteAsync(string directory, string name, Stream content, FileEntry? like, CancellationToken ct) =>
        throw new InvalidOperationException("An image's files are read-only.");

    public async ValueTask DisposeAsync()
    {
        // The engine may have stopped, which took the holder with it.
        try { await client.RemoveContainerAsync(holder, force: true); }
        catch (Exception e) when (e is DockerException or EngineUnavailableException) { }
    }
}
