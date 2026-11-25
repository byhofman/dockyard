using System.Runtime.Versioning;
using Dockyard.Shared;

namespace Dockyard.Adapter;

[SupportedOSPlatform("windows")]
static class Program
{
    static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "adapter.log");

    static async Task<int> Main(string[] args)
    {
        // One per user: the pipe names are this user's, and several can be started at once (logon, the Toolbox, setup).
        using var single = new Mutex(initiallyOwned: true, $@"Local\dockyard-adapter-{Environment.UserName}", out var first);
        if (!first) return 0;
        using var adapter = new WindowsAdapter(Log);
        using var stop = new CancellationTokenSource();
        Log("Started");
        await adapter.Attach();
        if (args.Contains("--domains")) await adapter.Handle("domains on");
        try { await AdapterPipe.Serve(adapter.Handle, stop.Token); }
        catch (OperationCanceledException) { }
        return 0;
    }

    static readonly object LogGate = new();

    static void Log(string line)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                if (File.Exists(LogFile) && new FileInfo(LogFile).Length > 1_000_000) File.Delete(LogFile);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
