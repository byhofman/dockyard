using Dockyard.Core;

namespace Dockyard.ViewModels;

sealed class EngineViewModel(Engine engine, string title, string subtitle, string endpoint) : Observable
{
    bool isRunning;
    bool isActive;

    public Engine Engine { get; } = engine;
    public string Title { get; } = title;
    public string Subtitle { get; set; } = subtitle;
    public string Endpoint { get; } = endpoint;
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

sealed class ComponentViewModel(Component component) : Observable
{
    string installed = "–";
    string latest = "–";
    UpdateState state;
    string? error;

    public Component Component { get; } = component;
    public string Title { get; } = Updates.Title(component);
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
