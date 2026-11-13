namespace Dockyard.Engine;

public sealed class ContainerConfig
{
    public string Image { get; set; } = "";
    public List<string>? Env { get; set; }
    public List<string>? Cmd { get; set; }
    public List<string>? Entrypoint { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
    public bool Tty { get; set; }
    public string? WorkingDir { get; set; }
}
