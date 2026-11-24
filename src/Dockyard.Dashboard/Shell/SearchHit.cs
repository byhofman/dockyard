namespace Dockyard.Dashboard.Shell;

sealed record SearchHit(string Kind, string Title, string Detail, Section Section, object Row, bool StartsGroup)
{
    public override string ToString() => $"{Kind}: {Title}";

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
                hits.AddRange(group.Take(PerKind).Select((r, i) => new SearchHit(group.Key, r.Title, r.Detail, section, r.Row, i == 0)));
        }
        return hits;
    }
}
