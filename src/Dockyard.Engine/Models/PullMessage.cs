namespace Dockyard.Engine;

sealed class PullMessage
{
    public string? Status { get; set; }
    public string? Id { get; set; }
    public ProgressDetail? ProgressDetail { get; set; }
    public string? Error { get; set; }
}
