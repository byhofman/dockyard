namespace Dockyard.Engine;

public sealed class ContainerInspect
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Created { get; set; } = "";
    public string Platform { get; set; } = "";
    public ContainerState State { get; set; } = new();
    public ContainerConfig Config { get; set; } = new();
    public HostConfigInfo HostConfig { get; set; } = new();
    public NetworkSettingsInfo NetworkSettings { get; set; } = new();
    public List<MountPoint> Mounts { get; set; } = [];
}
