using System.Text.RegularExpressions;
using Dockyard.Engine.Transport;

namespace Dockyard.Dashboard.Features.Resources;

public enum HostOs { Windows, MacOS, Linux }

// Where a volume's folder is as this computer sees it, or why it has none. Path is only ever built from a mountpoint
// that has exactly the engine's own shape, <data-root>/volumes/<name>/_data with a valid volume name and no "." or ".."
// anywhere, so nothing an engine reports can send the file manager somewhere else.
sealed partial record VolumeLocation(string? Path, string Note)
{
    public const string WslDistro = "dockyard";

    public static HostOs CurrentHost => OperatingSystem.IsWindows() ? HostOs.Windows : OperatingSystem.IsMacOS() ? HostOs.MacOS : HostOs.Linux;

    public static string FileManager(HostOs host) => host switch
    {
        HostOs.Windows => "File Explorer",
        HostOs.MacOS => "Finder",
        _ => "the file manager",
    };

    public static VolumeLocation For(HostOs host, string context, EndpointKind endpoint, bool windowsEngine, string name, string driver, string mountpoint)
    {
        if (driver != "local")
            return new(null, $"The {driver} driver keeps this volume's data itself, so there is no folder of it to open. Export a copy instead.");
        if (!VolumeName().IsMatch(name))
            return new(null, "This volume's name cannot be a folder here. Export a copy instead.");

        switch (host)
        {
            case HostOs.MacOS:
                return new(null, "Volumes live on the disk of Dockyard's Linux VM, not on this Mac's, so Finder cannot open them where they are. Export a copy to a folder and it opens in Finder.");
            case HostOs.Windows when windowsEngine && endpoint == EndpointKind.NamedPipe && IsWindowsVolumePath(mountpoint, name):
                return new(mountpoint, "On this computer's disk, in the Windows engine's folder, which normally only administrators can open.");
            case HostOs.Windows when !windowsEngine && context == "linux" && endpoint == EndpointKind.NamedPipe && IsLinuxVolumePath(mountpoint, name):
                return new($@"\\wsl.localhost\{WslDistro}" + mountpoint.Replace('/', '\\'), "Opens through WSL, in Dockyard's Linux engine. What you change there changes the volume.");
            case HostOs.Linux when !windowsEngine && endpoint == EndpointKind.Unix && IsLinuxVolumePath(mountpoint, name):
                return new(mountpoint, "On this computer's disk, in Docker's folder, which normally only root can open.");
            default:
                return new(null, "This engine keeps its volumes on another machine, so there is no folder of it here. Export a copy instead.");
        }
    }

    static bool IsLinuxVolumePath(string path, string name) =>
        path.StartsWith('/') && path.EndsWith($"/volumes/{name}/_data", StringComparison.Ordinal) && !path.Contains('\\') && Plain(path.Split('/')[1..]);

    static bool IsWindowsVolumePath(string path, string name) =>
        WindowsRoot().IsMatch(path) && path.EndsWith($@"\volumes\{name}\_data", StringComparison.OrdinalIgnoreCase) && !path.Contains('/') && Plain(path.Split('\\')[1..]);

    static bool Plain(string[] segments) =>
        segments.All(s => s.Length > 0 && s != "." && s != ".." && !s.Contains('\0') && !s.Contains(':'));

    // Docker's own rule for volume names.
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9_.-]*$")]
    private static partial Regex VolumeName();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsRoot();
}
