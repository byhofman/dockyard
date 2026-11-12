using System.Text.Json;

namespace Dockyard.Dashboard.Features.Resources;

sealed class DashboardSettings
{
    const int MaxRecent = 10;

    public List<RecentCompose> RecentCompose { get; set; } = [];
    public Dictionary<string, double> PanelSizes { get; set; } = [];

    static DashboardSettings? current;
    public static DashboardSettings Current => current ??= Load();

    public double PanelSize(string key, double fallback) => PanelSizes.TryGetValue(key, out var v) && v > 0 ? v : fallback;

    public void SetPanelSize(string key, double value)
    {
        PanelSizes[key] = Math.Round(value);
        Save();
    }

    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "dashboard.json");

    public static DashboardSettings Load()
    {
        try { return JsonSerializer.Deserialize(System.IO.File.ReadAllText(File), DashboardJson.Default.DashboardSettings) ?? new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, DashboardJson.Default.DashboardSettings));
        }
        catch (IOException) { }
    }

    public void AddRecent(string path, string project)
    {
        RecentCompose.RemoveAll(r => ComposeCatalog.SamePath(r.Path, path));
        RecentCompose.Insert(0, new RecentCompose { Path = path, Project = project });
        if (RecentCompose.Count > MaxRecent) RecentCompose.RemoveRange(MaxRecent, RecentCompose.Count - MaxRecent);
    }
}
