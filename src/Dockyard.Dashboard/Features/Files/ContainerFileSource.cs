using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

// A container's files, or a volume's through a holder that never runs; each folder is read from the engine when opened.
sealed class ContainerFileSource(DockerClient client, string container, string root, bool canWrite, Func<Task>? release = null) : IFileSource
{
    public string Root => root;
    public bool CanWrite => canWrite;

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string directory, IProgress<string> status, CancellationToken ct) =>
        await ContainerFiles.ListAsync(client, container, directory, ct);

    public Task<FileContent> ReadAsync(string path, int maxBytes, CancellationToken ct) =>
        ContainerFiles.ReadAsync(client, container, path, maxBytes, ct);

    public Task SaveToAsync(string path, Stream destination, CancellationToken ct) =>
        ContainerFiles.SaveToAsync(client, container, path, destination, ct);

    public Task WriteAsync(string directory, string name, Stream content, FileEntry? like, CancellationToken ct)
    {
        if (!canWrite) throw new InvalidOperationException("These files are read-only.");
        return ContainerFiles.WriteAsync(client, container, directory, name, content, like, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (release is not null) await release();
    }
}
