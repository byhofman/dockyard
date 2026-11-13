using Dockyard.Hosts;

namespace Dockyard.ViewModels;

sealed class EngineViewModel(EngineInfo info) : Observable
{
    bool isRunning;
    bool isActive;

    public EngineInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Title => Info.Title;
    public string Subtitle => Info.Subtitle;
    public string Endpoint => Info.Endpoint;
    public required Command ToggleCommand { get; init; }
    public required Command UseCommand { get; init; }

    public bool IsRunning
    {
        get => isRunning;
        set { if (Set(ref isRunning, value)) Raise(nameof(StatusText), nameof(ToggleText), nameof(IsStopped)); }
    }

    public bool IsActive
    {
        get => isActive;
        set { if (Set(ref isActive, value)) Raise(nameof(IsInactive)); }
    }

    bool isAvailable = true;
    public bool IsAvailable { get => isAvailable; set => Set(ref isAvailable, value); }
    public bool IsStopped => !IsRunning;
    public bool IsInactive => !IsActive;
    public string StatusText => IsRunning ? "Running" : "Stopped";
    public string ToggleText => IsRunning ? "Stop" : "Start";
}
