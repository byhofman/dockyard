using Dockyard.Hosts;

namespace Dockyard.Core;

// What can be added and removed, on what grounds, and what stays removed across a rerun of the wizard.
static class ComponentRules
{
    public const string DashboardId = "Dashboard";

    // Every name --with and --without accept on any system. Whether it exists on this one is a host's catalog's business.
    public static readonly string[] KnownFlags = ["dashboard", "compose", "buildx", "kind", "linux-engine", "windows-engine"];

    public static ComponentInfo? ByFlag(IEnumerable<ComponentInfo> catalog, string flag) => catalog.FirstOrDefault(c => c.Flag == flag);

    // Why this cannot be removed right now, or null when it can. present: the ids installed at the moment.
    public static string? WhyNotRemovable(IReadOnlyList<ComponentInfo> catalog, string id, IReadOnlySet<string> present)
    {
        var component = catalog.FirstOrDefault(c => c.Id == id);
        if (component is null) return $"There is no {id} here";
        if (component.RequiredBecause is { } reason) return $"{component.Title} is required: {reason}";
        if (component.IsEngine && !catalog.Any(c => c.IsEngine && c.Id != id && present.Contains(c.Id)))
            return $"{component.Title} is the last engine left, and Dockyard does nothing without one. Uninstall removes Dockyard altogether.";
        return null;
    }

    // Problems with a silent request, before anything changes.
    public static IReadOnlyList<string> Problems(IReadOnlyList<ComponentInfo> catalog, IReadOnlyList<string> with, IReadOnlyList<string> without, IReadOnlyCollection<string> removedBefore)
    {
        List<string> problems = [];
        foreach (var flag in with.Concat(without))
            if (ByFlag(catalog, flag) is null && catalog.All(c => c.RequiredFlag != flag)) problems.Add($"{flag} does not exist on this system");
        foreach (var flag in without)
            if (catalog.FirstOrDefault(c => c.RequiredFlag == flag) is { RequiredBecause: { } reason } required) problems.Add($"{required.Title} is required: {reason}");

        var gone = new HashSet<string>(removedBefore);
        foreach (var flag in without) if (ByFlag(catalog, flag) is { } c) gone.Add(c.Id);
        foreach (var flag in with) if (ByFlag(catalog, flag) is { } c) gone.Remove(c.Id);
        var engines = catalog.Where(c => c.IsEngine).ToList();
        if (engines.Count > 0 && engines.All(e => gone.Contains(e.Id)))
            problems.Add("At least one engine has to stay. Uninstall removes Dockyard altogether.");
        return problems;
    }

    public static List<string> Removed(IEnumerable<ComponentInfo> catalog, IEnumerable<string> before, IEnumerable<string> with, IEnumerable<string> without)
    {
        var list = catalog.ToList();
        var removed = new HashSet<string>(before);
        foreach (var flag in without) if (ByFlag(list, flag) is { } c) removed.Add(c.Id);
        foreach (var flag in with) if (ByFlag(list, flag) is { } c) removed.Remove(c.Id);
        // The dashboard has its own way of staying gone: whether its files are there.
        removed.Remove(DashboardId);
        return removed.Order().ToList();
    }
}
