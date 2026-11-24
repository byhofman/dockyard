using Avalonia.Media;

namespace Dockyard.Dashboard.Shell;

// A row of the search dropdown: a kind's header, or one thing that matches with its section's icon.
sealed record SearchHit(string Kind, string Title, string Detail, Section? Section, object? Row, Geometry? Icon, bool IsHeader = false, int Total = 0)
{
    public override string ToString() => IsHeader ? Kind : $"{Kind}: {Title}";

    public bool IsSelectable => !IsHeader;
    public string HeaderText => Kind.ToUpperInvariant();
    public string TotalText => Total > PerKind ? $"{PerKind} of {Total}" : Total.ToString();

    public const int PerKind = 5;

    // Grouped by kind in the sidebar's order, a few of each, so one kind with many matches does not hide the others.
    public static List<SearchHit> From(string text, IEnumerable<Section> sections)
    {
        var hits = new List<SearchHit>();
        if (text.Trim().Length == 0) return hits;
        foreach (var section in sections)
        {
            if (section.Content is not ISearchable searchable) continue;
            foreach (var group in searchable.Find(text.Trim()).GroupBy(r => r.Kind ?? section.Title))
            {
                var all = group.ToList();
                hits.Add(new SearchHit(group.Key, "", "", null, null, null, IsHeader: true, Total: all.Count));
                hits.AddRange(all.Take(PerKind).Select(r => new SearchHit(group.Key, r.Title, r.Detail, section, r.Row,
                    r.Icon is { } key ? Section.Resource(key) ?? section.Icon : section.Icon)));
            }
        }
        return hits;
    }
}
