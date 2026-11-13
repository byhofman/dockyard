namespace Dockyard.Engine;

public sealed class DockerEvent
{
    public string Type { get; set; } = "";
    public string Action { get; set; } = "";
    public EventActor Actor { get; set; } = new();
    public long Time { get; set; }
    public long TimeNano { get; set; }
}
