using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

sealed record FileRow(FileEntry Entry, string Directory)
{
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public string SizeText => Entry.IsDirectory ? "" : Format.Bytes(Entry.Size);
    public string Detail => Entry.LinkTarget is { } target ? "→ " + target : Entry.Modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string Path => ContainerFiles.Combine(Directory, Name);
}
