namespace Dockyard.Engine;

public sealed class CreateContainerRequest
{
    public string Image { get; set; } = "";
    public List<string>? Cmd { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}
