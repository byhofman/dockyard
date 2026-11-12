namespace Dockyard.Engine;

public sealed class DfContainer
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public long SizeRw { get; set; }
    public long SizeRootFs { get; set; }
    public string State { get; set; } = "";
}
