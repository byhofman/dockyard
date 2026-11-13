namespace Dockyard.Engine;

public sealed class EventActor
{
    public string ID { get; set; } = "";
    public Dictionary<string, string>? Attributes { get; set; }
}
