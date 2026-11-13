namespace Dockyard.Dashboard.Store;

static class Backoff
{
    static readonly TimeSpan First = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan Max = TimeSpan.FromSeconds(10);

    public static TimeSpan Next(TimeSpan? previous) =>
        previous is { } p ? TimeSpan.FromTicks(Math.Min(p.Ticks * 2, Max.Ticks)) : First;
}
