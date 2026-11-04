namespace Dockyard.Engine;

public sealed class Volume
{
    public string Name { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Mountpoint { get; set; } = "";
    public string? CreatedAt { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public string Scope { get; set; } = "";
    public VolumeUsage? UsageData { get; set; }
}

public sealed class VolumeUsage
{
    public long Size { get; set; }
    public long RefCount { get; set; }
}

sealed class VolumeList
{
    public List<Volume>? Volumes { get; set; }
}
