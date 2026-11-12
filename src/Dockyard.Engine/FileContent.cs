namespace Dockyard.Engine;

public sealed record FileContent(byte[] Data, bool Truncated, FileEntry Entry);
