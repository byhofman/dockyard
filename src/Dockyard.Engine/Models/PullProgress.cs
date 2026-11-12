namespace Dockyard.Engine;

public sealed record PullProgress(string Status, long CurrentBytes, long TotalBytes);
