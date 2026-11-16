using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

interface IFileSource : IAsyncDisposable
{
    string Root { get; }
    bool CanWrite { get; }
    Task<IReadOnlyList<FileEntry>> ListAsync(string directory, IProgress<string> status, CancellationToken ct);
    Task<FileContent> ReadAsync(string path, int maxBytes, CancellationToken ct);
    Task SaveToAsync(string path, Stream destination, CancellationToken ct);
    Task WriteAsync(string directory, string name, Stream content, FileEntry? like, CancellationToken ct);
}
