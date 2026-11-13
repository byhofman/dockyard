namespace Dockyard.Dashboard.Features.Resources;

sealed record ComposeProject(string Name, string? WorkingDir, IReadOnlyList<string> ConfigFiles, IReadOnlyList<ComposeService> Services)
{
    public int Running => Services.Sum(s => s.Running);
    public int Total => Services.Sum(s => s.Containers.Count);
    public string Summary => Total == 0 ? "not running" : $"{Running}/{Total} running";
    public string Location => ConfigFiles.Count > 0 ? ConfigFiles[0] : WorkingDir ?? "";
    public bool CanUp => ConfigFiles.Count > 0;
    public bool IsRunning => Running > 0;
}
