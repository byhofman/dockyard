namespace Dockyard.Engine;

public sealed class ContainerState
{
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public int ExitCode { get; set; }
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public HealthState? Health { get; set; }
}
