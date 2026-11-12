namespace Dockyard.Dashboard.Features.Containers;

static class StatsSelection
{
    public const int Limit = 50;

    public static HashSet<string> Choose(IReadOnlyCollection<string> running, IReadOnlySet<string> visible, int limit = Limit) =>
        running.Count <= limit ? running.ToHashSet() : running.Where(visible.Contains).ToHashSet();
}
