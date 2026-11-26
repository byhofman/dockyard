namespace Dockyard.Hosts;

sealed record Release(Version Version, string Tag, string? DownloadUrl);
