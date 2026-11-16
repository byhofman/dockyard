using System.Diagnostics;

namespace Dockyard.Dashboard.Features.Files;

static class FileManager
{
    // Whether this user can see into the folder at all; the engine's folders are often root's or administrators' only.
    public static bool CanOpen(string folder)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return false; }
    }

    // Only ever an existing folder, so the shell opens it rather than running anything.
    public static void Open(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows()) info = new ProcessStartInfo(folder) { UseShellExecute = true };
        else
        {
            info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            info.ArgumentList.Add(folder);
        }
        Process.Start(info)?.Dispose();
    }
}
