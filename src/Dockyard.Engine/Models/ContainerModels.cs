namespace Dockyard.Engine;

public sealed class ContainerSummary
{
    public string Id { get; set; } = "";
    public List<string> Names { get; set; } = [];
    public string Image { get; set; } = "";
    public string ImageID { get; set; } = "";
    public string Command { get; set; } = "";
    public long Created { get; set; }
    public string State { get; set; } = "";
    public string Status { get; set; } = "";
    public List<PortMapping> Ports { get; set; } = [];
    public Dictionary<string, string>? Labels { get; set; }

    public string Name => Names.Count > 0 ? Names[0].TrimStart('/') : Id[..Math.Min(12, Id.Length)];
}

public sealed class PortMapping
{
    public string? IP { get; set; }
    public int PrivatePort { get; set; }
    public int? PublicPort { get; set; }
    public string Type { get; set; } = "";
}

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

public sealed class ContainerState
{
    public string Status { get; set; } = "";
    public bool Running { get; set; }
    public int ExitCode { get; set; }
    public string StartedAt { get; set; } = "";
    public string FinishedAt { get; set; } = "";
    public HealthState? Health { get; set; }
}

public sealed class HealthState
{
    public string Status { get; set; } = "";
}

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

public sealed class HostConfigInfo
{
    public RestartPolicy? RestartPolicy { get; set; }
}

public sealed class RestartPolicy
{
    public string Name { get; set; } = "";
}

public sealed class NetworkSettingsInfo
{
    public Dictionary<string, List<PortBinding>?>? Ports { get; set; }
    public Dictionary<string, EndpointSettings>? Networks { get; set; }
}

public sealed class PortBinding
{
    public string HostIp { get; set; } = "";
    public string HostPort { get; set; } = "";
}

public sealed class EndpointSettings
{
    public string IPAddress { get; set; } = "";
    public string NetworkID { get; set; } = "";
}

public sealed class MountPoint
{
    public string Type { get; set; } = "";
    public string? Name { get; set; }
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public bool RW { get; set; }
}

public sealed class CreateContainerRequest
{
    public string Image { get; set; } = "";
    public List<string>? Cmd { get; set; }
    public Dictionary<string, string>? Labels { get; set; }
}

sealed class CreateContainerResponse
{
    public string Id { get; set; } = "";
}
