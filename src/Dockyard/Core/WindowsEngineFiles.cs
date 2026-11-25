namespace Dockyard.Core;

// What shares C:\Program Files\Docker with the Windows engine but stays when the engine goes.
static class WindowsEngineFiles
{
    public static bool Keep(string name) =>
        name.Equals("docker.exe", StringComparison.OrdinalIgnoreCase)
        || Tools.Standalone.Any(t => name.Equals(t.Command + ".exe", StringComparison.OrdinalIgnoreCase))
        || name.StartsWith("docker-credential", StringComparison.OrdinalIgnoreCase);
}
