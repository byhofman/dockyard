namespace Dockyard.Dashboard.Shell;

interface ISearchable
{
    void ApplySearch(string text);

    // Everything that matches, whatever the section's own filter is; Kind overrides the section's title.
    IEnumerable<SearchResult> Find(string text) => [];

    void Reveal(object row) { }
}

sealed record SearchResult(string Title, string Detail, object Row, string? Kind = null, string? Icon = null);
