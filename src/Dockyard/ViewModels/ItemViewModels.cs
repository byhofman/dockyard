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

    public bool IsStopped => !IsRunning;
    public bool IsInactive => !IsActive;
    public string StatusText => IsRunning ? "Running" : "Stopped";
    public string ToggleText => IsRunning ? "Stop" : "Start";
}

enum UpdateState { Unknown, Checking, UpToDate, Available, NotInstalled, NoRelease, Error }

sealed class ComponentViewModel(ComponentInfo info) : Observable
{
    string installed = "–";
    string latest = "–";
    UpdateState state;
    string? error;

    public ComponentInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Title => Info.Title;
    public required Command UpdateCommand { get; init; }
    public Release? LatestRelease { get; set; }

    public string Installed { get => installed; set => Set(ref installed, value); }
    public string Latest { get => latest; set => Set(ref latest, value); }
    public string? Error { get => error; set => Set(ref error, value); }

    public UpdateState State
    {
        get => state;
        set
        {
            if (Set(ref state, value))
                Raise(nameof(StatusText), nameof(IsAvailable), nameof(IsUpToDate), nameof(IsProblem), nameof(IsNeutral));
        }
    }

    public bool IsAvailable => State == UpdateState.Available;
    public bool IsUpToDate => State == UpdateState.UpToDate;
    public bool IsProblem => State is UpdateState.Error or UpdateState.NotInstalled;
    public bool IsNeutral => State is UpdateState.Unknown or UpdateState.Checking;

    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking…",
        UpdateState.UpToDate => "Up to date",
        UpdateState.Available => "Update available",
        UpdateState.NotInstalled => "Not installed",
        UpdateState.NoRelease => "No release",
        UpdateState.Error => "Check failed",
        _ => "Not checked",
    };
}
