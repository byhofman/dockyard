namespace Dockyard.Dashboard.Features.Containers;

sealed record InspectSection(string Title, IReadOnlyList<KeyValue> Items)
{
    public string CopyText => string.Join(Environment.NewLine, Items.Select(i => i.Key.Length > 0 ? $"{i.Key}={i.Value}" : i.Value));
}
