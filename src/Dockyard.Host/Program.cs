using System.Runtime.Versioning;
using Dockyard.Shared;

namespace Dockyard.Host;

[SupportedOSPlatform("windows")]
static class Program
{
    static readonly string LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "host.log");

    static async Task<int> Main(string[] args)
    {
        // One per user: the pipe names are this user's, and several can be started at once (logon, the tray, setup).
        using var single = new Mutex(initiallyOwned: true, $@"Local\dockyard-host-{Environment.UserName}", out var first);
        if (!first) return 0;
        using var host = new WslEngineHost(Log);
        using var stop = new CancellationTokenSource();
        Log("Started");
        await host.Attach();
        if (args.Contains("--domains") || File.Exists(WslEngineHost.DomainsMarker)) await host.Handle("domains on");
        try { await HostPipe.Serve(host.Handle, stop.Token); }
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
