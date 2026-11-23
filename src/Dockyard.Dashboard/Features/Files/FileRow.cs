using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Files;

sealed record FileRow(FileEntry Entry, string Directory)
{
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;
    public bool IsFile => !Entry.IsDirectory;
    public bool IsLink => Entry.LinkTarget is not null;
    public string LinkText => Entry.LinkTarget is { } target ? "→ " + target : "";
    public string SizeText => Entry.IsDirectory && Entry.Size == 0 ? "" : Format.Bytes(Entry.Size);
    public string ModifiedText => Entry.Modified == default ? "" : Format.Age(Entry.Modified, DateTimeOffset.UtcNow);
    public string ModifiedTip => Entry.Modified == default ? "" : Entry.Modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string Path => ContainerFiles.Combine(Directory, Name);
    public override string ToString() => Name;
}
