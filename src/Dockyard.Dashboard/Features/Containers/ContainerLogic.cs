using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

sealed record ProcessCommand(string File, IReadOnlyList<string> Args);

static class ExecCommand
{
    const string LinuxShell = "command -v bash >/dev/null && exec bash || exec sh";
    const string WindowsShell = "where powershell >nul 2>&1 && powershell || cmd";

    public static ProcessCommand Build(string context, string id, string title, bool windowsContainer, string? windowsTerminal)
    {
        List<string> docker = ["--context", context, "exec", "-it", id];
        docker.AddRange(windowsContainer ? ["cmd", "/c", WindowsShell] : ["sh", "-c", LinuxShell]);

        if (windowsTerminal is null) return new ProcessCommand("docker", docker);
        return new ProcessCommand(windowsTerminal, ["-w", "new", "--title", title, "--", "docker", .. docker]);
    }

    public static string? FindWindowsTerminal()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        return File.Exists(path) ? path : null;
    }
}

sealed class LogBuffer(int capacity = 10_000)
{
    readonly Queue<LogLine> lines = new();

    public int Capacity { get; } = capacity;
    public int Count => lines.Count;
    public IEnumerable<LogLine> Lines => lines;

    public bool Add(LogLine line)
    {
        var dropped = false;
        if (lines.Count == Capacity)
        {
            lines.Dequeue();
            dropped = true;
        }
        lines.Enqueue(line);
        return dropped;
    }

    public void Clear() => lines.Clear();

    public List<LogLine> Filtered(string? search) => string.IsNullOrEmpty(search)
        ? lines.ToList()
        : lines.Where(l => Matches(l, search)).ToList();

    public static bool Matches(LogLine line, string? search) =>
        string.IsNullOrEmpty(search) || line.Text.Contains(search, StringComparison.OrdinalIgnoreCase);
}

static class StatsSelection
{
    public const int Limit = 50;

    public static HashSet<string> Choose(IReadOnlyCollection<string> running, IReadOnlySet<string> visible, int limit = Limit) =>
        running.Count <= limit ? running.ToHashSet() : running.Where(visible.Contains).ToHashSet();
}
