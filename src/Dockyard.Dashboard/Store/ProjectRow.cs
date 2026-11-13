namespace Dockyard.Dashboard.Store;

sealed record ProjectRow(Features.Resources.ComposeProject Project, bool IsExpanded)
{
    public string Name => Project.Name;
    public string Summary => Project.Summary;
    public string Location => Project.Location;
    public bool IsRunning => Project.IsRunning;
    public string Chevron => IsExpanded ? "25BE" : "25B8";
}
