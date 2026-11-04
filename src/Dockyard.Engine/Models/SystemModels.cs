namespace Dockyard.Engine;

public sealed class SystemDf
{
    public long LayersSize { get; set; }
    public List<ImageSummary>? Images { get; set; }
    public List<DfContainer>? Containers { get; set; }
    public List<Volume>? Volumes { get; set; }
    public List<BuildCacheRecord>? BuildCache { get; set; }
}

public sealed class DfContainer
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public long SizeRw { get; set; }
    public long SizeRootFs { get; set; }
    public string State { get; set; } = "";
}

public sealed class BuildCacheRecord
{
    public string ID { get; set; } = "";
    public long Size { get; set; }
    public bool InUse { get; set; }
    public bool Shared { get; set; }
}
