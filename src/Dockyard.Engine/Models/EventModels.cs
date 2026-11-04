namespace Dockyard.Engine;

public sealed class DockerEvent
{
    public string Type { get; set; } = "";
    public string Action { get; set; } = "";
    public EventActor Actor { get; set; } = new();
    public long Time { get; set; }
    public long TimeNano { get; set; }
}

public sealed class EventActor
{
    public string ID { get; set; } = "";
    public Dictionary<string, string>? Attributes { get; set; }
}
