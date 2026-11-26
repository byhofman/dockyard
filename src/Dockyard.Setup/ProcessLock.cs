using Dockyard.Core;

namespace Dockyard.Setup;

// Held by the one process of this user that does something: an open file nobody else may open. The system lets go of it when
// the process ends, however it ends, so a crash never leaves the work without a taker. Not a named pipe, which on Unix a
// second server takes over, and not a named mutex, which Native AOT does not share between processes on macOS.
sealed class ProcessLock : IDisposable
{
    public static string DutiesFile => Path.Combine(Paths.SettingsDir, "duties.lock");
    public static string TrayFile => Path.Combine(Paths.SettingsDir, "tray.lock");

    readonly FileStream file;

    ProcessLock(FileStream file) => this.file = file;

    public static ProcessLock? TryTake(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new ProcessLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException) { return null; }
    }

    public static bool IsHeld(string path)
    {
        using var taken = TryTake(path);
        return taken is null;
    }

    public void Dispose() => file.Dispose();
}
