namespace Dockyard.Engine;

public sealed record FileEntry(string Name, bool IsDirectory, long Size, string? LinkTarget, DateTimeOffset Modified, UnixFileMode Mode, int Uid, int Gid);
