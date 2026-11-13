using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Containers;

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
