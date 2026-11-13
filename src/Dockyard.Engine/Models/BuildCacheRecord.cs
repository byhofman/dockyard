namespace Dockyard.Engine;

public sealed class BuildCacheRecord
{
    public string ID { get; set; } = "";
    public long Size { get; set; }
    public bool InUse { get; set; }
    public bool Shared { get; set; }
}
