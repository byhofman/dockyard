using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The Dockyard source's tools in Program Files\Docker, for a Windows without winget: changing them takes the administrator step.
[SupportedOSPlatform("windows")]
sealed class WindowsDockyardSource : IDockyardSource
{
    public Task<Version?> Installed(CliTool tool) => Updates.Installed(Enum.Parse<Component>(tool.Id));

    public Release? Pinned(CliTool tool) => tool.Id switch
    {
        nameof(Component.DockerCli) => Updates.WindowsDocker,
        nameof(Component.Compose) => Updates.WindowsCompose,
        nameof(Component.Buildx) => Updates.WindowsBuildx,
        _ => Tools.Pinned(tool),
    };

    public Task Install(Action<string> log, CliTool tool) => WindowsSetup.MachineComponent(log, tool.Id, installed: true);
    public Task Remove(Action<string> log, CliTool tool) => WindowsSetup.MachineComponent(log, tool.Id, installed: false);
}
