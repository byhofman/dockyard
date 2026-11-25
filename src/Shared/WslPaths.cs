namespace Dockyard.Shared;

// The Linux engine in WSL as Windows reaches it: shared by the Toolbox, which sets it up, and dockyard-host, which keeps it
// running and carries Windows' traffic to it.
static class WslPaths
{
    public const string DistroName = "dockyard";
    public const string InitPath = "/usr/local/sbin/dockyard-init";
    // Where dockyard-agent serves the Linux engine to Windows, to the token only this user can read.
    public const int LinuxPort = 2375;
    public const string LinuxPipeName = "dockyard_linux";
    public const string LinuxAgentPipeName = "dockyard_linux_agent";
    public const string LinuxHost = "npipe:////./pipe/" + LinuxPipeName;
    public static readonly string EngineTokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "engine.token");
}
