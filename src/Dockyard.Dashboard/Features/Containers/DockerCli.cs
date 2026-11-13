namespace Dockyard.Dashboard.Features.Containers;

static class DockerCli
{
    // An app started from the menu bar or at login does not get the PATH of a shell, so on macOS "docker" alone is not found.
    public static string Path(string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var own = System.IO.Path.Combine(home, ".dockyard", "bin", "docker");
        return !OperatingSystem.IsWindows() && File.Exists(own) ? own : "docker";
    }
}
