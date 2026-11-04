namespace Dockyard.Engine;

public sealed class ImageSummary
{
    public string Id { get; set; } = "";
    public string? ParentId { get; set; }
    public List<string>? RepoTags { get; set; }
    public List<string>? RepoDigests { get; set; }
    public long Created { get; set; }
    public long Size { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public long Containers { get; set; }
}

public sealed record PullProgress(string Status, long CurrentBytes, long TotalBytes);

sealed class PullMessage
{
    public string? Status { get; set; }
    public string? Id { get; set; }
    public ProgressDetail? ProgressDetail { get; set; }
    public string? Error { get; set; }
}

sealed class ProgressDetail
{
    public long Current { get; set; }
    public long Total { get; set; }
}
