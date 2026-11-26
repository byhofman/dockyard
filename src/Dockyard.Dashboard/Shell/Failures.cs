namespace Dockyard.Dashboard.Shell;

// What went wrong where nothing caught it: written to a log of its own, so a crash leaves a trace to report, and shown
// when the window is up.
static class Failures
{
    const long MaxLogBytes = 1024 * 1024;
    static readonly object gate = new();

    public static string LogFile { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "logs", "dashboard-errors.log");

    public static Action<string>? Show { get; set; }

    public static void Report(Exception ex, string where, bool show = true)
    {
        Write(ex, where);
        if (show) Show?.Invoke($"Something went wrong: {ex.Message}. The details are in {LogFile}.");
    }

    public static void Write(Exception ex, string where)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                var file = new FileInfo(LogFile);
                if (file.Exists && file.Length > MaxLogBytes) File.Move(LogFile, LogFile + ".1", overwrite: true);
                File.AppendAllText(LogFile, $"{DateTimeOffset.Now:O} {where} (dashboard {AppVersion}, {Environment.OSVersion})\n{ex}\n\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string AppVersion => (typeof(Failures).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0];
}
