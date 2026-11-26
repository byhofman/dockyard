using Dockyard.Hosts;

namespace Dockyard.Core;

// The tools in the administrator step: winget for the whole machine where it may be used and Windows runs what it brings,
// the source Dockyard, in Program Files\Docker, otherwise. Runs elevated.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static class MachineTools
{
    sealed class DockyardSource : IDockyardSource
    {
        public Task<Version?> Installed(CliTool tool) => Updates.Installed(Enum.Parse<Component>(tool.Id));

        public Release? Pinned(CliTool tool) => tool.Id switch
        {
            nameof(Component.DockerCli) => Updates.WindowsDocker,
            nameof(Component.Compose) => Updates.WindowsCompose,
            nameof(Component.Buildx) => Updates.WindowsBuildx,
            _ => Tools.Pinned(tool),
        };

        public async Task Install(Action<string> log, CliTool tool)
        {
            switch (tool.Id)
            {
                case nameof(Component.DockerCli): await MachineEngine.UpdateCli(log, onlyIfNewer: false); break;
                case nameof(Component.Compose) or nameof(Component.Buildx): await MachineApps.UpdatePlugin(log, Enum.Parse<Component>(tool.Id)); break;
                default: await MachineApps.InstallTool(log, tool); break;
            }
        }

        public Task Remove(Action<string> log, CliTool tool)
        {
            switch (tool.Id)
            {
                case nameof(Component.DockerCli): MachineEngine.RemoveCli(log); break;
                case nameof(Component.Compose) or nameof(Component.Buildx): MachineApps.RemovePlugin(log, Enum.Parse<Component>(tool.Id)); break;
                default: MachineApps.RemoveTool(log, tool); break;
            }
            return Task.CompletedTask;
        }
    }

    public static ToolComponents For(ToolSource source) =>
        new(new Winget(ProcessRunner.Instance, MachineApps.ExcludeFromAsr), new DockyardSource(), () => source, Ledger);

    // In Program Files, next to the tools: winget runs in the administrator step, which may be another account.
    public static ToolLedger Ledger => new(Path.Combine(Paths.AppDir, "package-tools.json"));
}
