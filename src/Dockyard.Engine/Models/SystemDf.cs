namespace Dockyard.Engine;

public sealed class SystemDf
{
    public long LayersSize { get; set; }
    public List<ImageSummary>? Images { get; set; }
    public List<DfContainer>? Containers { get; set; }
    public List<Volume>? Volumes { get; set; }
    public List<BuildCacheRecord>? BuildCache { get; set; }
}
