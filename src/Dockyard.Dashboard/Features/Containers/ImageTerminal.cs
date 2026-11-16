using System.Text.Json.Nodes;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

// A throwaway container of an image, for one terminal: the image's shell instead of its own command, removed when the
// shell ends (AutoRemove) and, when the window is closed instead, by the watcher in ExecCommand.
static class ImageTerminal
{
    public const string Label = "dockyard.image-terminal";
    static readonly string[] LinuxShells = ["/bin/bash", "/bin/sh"];

    // Looked up in a container of the image that is never started, so an image without a shell is told apart before any
    // window opens. Every Windows base image has cmd.exe.
    public static async Task<string?> FindShellAsync(DockerClient client, string image, CancellationToken ct = default)
    {
        if (client.IsWindows) return "cmd.exe";
        var holder = await client.CreateImageHolderAsync(image, ct);
        try
        {
            foreach (var shell in LinuxShells)
                if (await client.PathExistsAsync(holder, shell, ct)) return shell;
            return null;
        }
        finally
        {
            try { await client.RemoveContainerAsync(holder, force: true, ct: CancellationToken.None); }
            catch (Exception e) when (e is DockerException or EngineUnavailableException) { }
        }
    }

    public static async Task<string> CreateAsync(DockerClient client, string image, string shell, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["Image"] = image,
            ["Entrypoint"] = new JsonArray(shell),
            ["Cmd"] = new JsonArray(),
            ["Tty"] = true,
            ["OpenStdin"] = true,
            ["StdinOnce"] = true,
            ["AttachStdin"] = true,
            ["AttachStdout"] = true,
            ["AttachStderr"] = true,
            ["Labels"] = new JsonObject { [Label] = image },
            ["HostConfig"] = new JsonObject { ["AutoRemove"] = true },
        };
        return (await client.PostNodeAsync("containers/create", body, ct))!["Id"]!.GetValue<string>();
    }
}
