using System.Collections.ObjectModel;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Store;

sealed class ContainerStore
{
    readonly Dictionary<string, ContainerItem> items = new();
    readonly HashSet<string> collapsed = [];
    List<Features.Resources.RecentCompose> recents = [];
    string filter = "";

    public ObservableCollection<object> Rows { get; } = [];
    public IReadOnlyCollection<ContainerItem> Items => items.Values;
    public event Action? RowsChanging;
    public event Action? RowsChanged;

    public string Filter
    {
        get => filter;
        set
        {
            if (filter == (value ?? "")) return;
            filter = value ?? "";
            Rebuild();
        }
    }

    public ContainerItem? Get(string id) => items.GetValueOrDefault(id);

    // Containers Dockyard creates for itself to hold a volume are never started and are not the user's.
    static bool IsHelper(ContainerSummary s) => s.Labels?.ContainsKey("dockyard.helper") == true;

    public void Reset(IEnumerable<ContainerSummary> all)
    {
        var containers = all.Where(c => !IsHelper(c)).ToList();
        var seen = new HashSet<string>();
        foreach (var s in containers)
        {
            seen.Add(s.Id);
            if (!items.TryGetValue(s.Id, out var item)) items[s.Id] = item = new ContainerItem(s.Id);
            item.Update(s);
        }
        foreach (var gone in items.Keys.Where(k => !seen.Contains(k)).ToList()) items.Remove(gone);
        Rebuild();
    }

    public void Upsert(ContainerSummary s)
    {
        if (IsHelper(s)) return;
        var isNew = !items.TryGetValue(s.Id, out var item);
        if (isNew) items[s.Id] = item = new ContainerItem(s.Id);
        var (name, project, running) = (item!.Name, item.Project, item.IsRunning);
        item.Update(s);
        if (isNew || name != item.Name || project != item.Project || running != item.IsRunning) Rebuild();
    }

    public void Remove(string id)
    {
        if (items.Remove(id)) Rebuild();
    }

    public void Clear()
    {
        items.Clear();
        Rebuild();
    }

    public void SetRecents(IEnumerable<Features.Resources.RecentCompose> value)
    {
        recents = value.ToList();
        Rebuild();
    }

    public void ToggleProject(string name)
    {
        if (!collapsed.Remove(name)) collapsed.Add(name);
        Rebuild();
    }

    public ProjectRow? FindProject(string name) => Rows.OfType<ProjectRow>().FirstOrDefault(p => p.Name == name);

    void Rebuild()
    {
        var rows = BuildRows(items.Values, filter, recents, collapsed);
        if (rows.SequenceEqual(Rows)) return;
        RowsChanging?.Invoke();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        RowsChanged?.Invoke();
    }

    internal static List<object> BuildRows(IEnumerable<ContainerItem> all, string filter,
        IEnumerable<Features.Resources.RecentCompose>? recents = null, IReadOnlySet<string>? collapsed = null)
    {
        var list = all.ToList();
        var projects = Features.Resources.ComposeCatalog.Build(list, recents ?? []);
        var entries = new List<(string Key, List<object> Rows)>();

        foreach (var p in projects)
        {
            var projectMatches = filter.Length == 0 || p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
            var children = p.Services.SelectMany(s => s.Containers)
                .Where(c => projectMatches || Matches(c, filter))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!projectMatches && children.Count == 0) continue;
            var expanded = collapsed?.Contains(p.Name) != true;
            List<object> rows = [new ProjectRow(p, expanded)];
            if (expanded) rows.AddRange(children);
            entries.Add((p.Name, rows));
        }
        foreach (var c in list.Where(i => i.Project.Length == 0 && Matches(i, filter)))
            entries.Add((c.Name, [c]));

        return entries.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase).SelectMany(e => e.Rows).ToList();
    }

    static bool Matches(ContainerItem i, string filter) =>
        filter.Length == 0
        || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || i.Image.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || i.Project.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
