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
