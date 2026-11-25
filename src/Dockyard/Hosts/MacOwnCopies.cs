using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Dockyard's own copies on a Mac without Homebrew: the CLI and the other tools in ~/.dockyard/bin, the plugins in
// ~/.docker/cli-plugins.
[SupportedOSPlatform("macos")]
sealed class MacOwnCopies(ICommandRunner runner, MacComponents parts) : IOwnCopies
{
    static string? Plugin(CliTool tool) => tool == Tools.Compose || tool == Tools.Buildx ? Path.Combine(MacLayout.PluginDir, tool.Command) : null;
    static string File(CliTool tool) => Plugin(tool) ?? Path.Combine(MacLayout.BinDir, tool.Command);

    // Dockyard's are plain files; a link in the plugin folder is another Docker app's.
    public async Task<Version?> Installed(CliTool tool) =>
        System.IO.File.Exists(File(tool)) && new FileInfo(File(tool)).LinkTarget is null
            ? Updates.ParseVersion((await runner.Run(File(tool), tool.VersionArgs)).Output)
            : null;

    public Release? Pinned(CliTool tool) => tool.Id switch
    {
        nameof(Component.Cli) => MacLayout.Cli,
        nameof(Component.Compose) => MacLayout.Compose,
        nameof(Component.Buildx) => MacLayout.Buildx,
        _ => Tools.Pinned(tool),
    };

    public async Task Install(Action<string> log, CliTool tool)
    {
        if (tool == Tools.Docker) await parts.InstallCli(log, MacLayout.Cli);
        else if (Plugin(tool) is not null) await MacComponents.InstallPlugin(log, tool.Command, Pinned(tool)!);
        else
        {
            Directory.CreateDirectory(MacLayout.BinDir);
            await Tools.Download(tool, File(tool));
            log($"{tool.Command} {Tools.Pinned(tool).Tag} in {MacLayout.BinDir}");
        }
    }

    public Task Remove(Action<string> log, CliTool tool)
    {
        if (Plugin(tool) is not null) MacComponents.RemovePlugin(tool.Command);
        else System.IO.File.Delete(File(tool));
        log($"Removed Dockyard's own {tool.Command}");
        return Task.CompletedTask;
    }
}
