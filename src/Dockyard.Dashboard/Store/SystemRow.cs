namespace Dockyard.Dashboard.Store;

// Dockyard's own containers, under one row at the end of the list: shown so nothing runs unseen, not managed from here.
sealed record SystemRow(int Running, int Total, bool IsExpanded)
{
    public const string Key = "dockyard:system";
    public const string HelperLabel = "dockyard.helper";
    public const string ServiceLabel = "dockyard.service";

    public string Name => "Dockyard";
    public string Location => "Dockyard's own containers; it starts and removes them itself";
    public string Summary => $"{Running}/{Total} running";
    public bool IsRunning => Running > 0;
    public override string ToString() => Name;
}
