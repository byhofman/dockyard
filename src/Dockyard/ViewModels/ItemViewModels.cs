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

enum UpdateState { Unknown, Checking, UpToDate, Available, NotInstalled, NoRelease, Error, OptionalInstalled, OptionalAbsent }

sealed class ComponentViewModel(ComponentInfo info) : Observable
{
    string installed = "–";
    string latest = "–";
    UpdateState state;
    string? error;
    bool isPresent;
    bool include = true;

    public ComponentInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Title => Info.Title;
    public required Command UpdateCommand { get; init; }
    public Command? AddCommand { get; init; }
    public Command? RemoveCommand { get; init; }
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

    public bool IsPresent { get => isPresent; set { if (Set(ref isPresent, value)) Raise(nameof(CanAdd), nameof(CanRemove)); } }
    // The first install's choice for this row.
    public bool Include { get => include; set => Set(ref include, value); }
    public bool IsOptional => Info.IsOptional;
    public string AddId => "Add-" + Id;
    public string RemoveId => "Remove-" + Id;
    public string UpdateId => "Update-" + Id;
    public string Note => Info.RequiredBecause is { } reason ? "Required: " + reason : Info.Note ?? "";
    public bool HasNote => Note.Length > 0;
    public string IncludeText => $"{Title}" + (Info.Note is { } note ? $". {note}" : "");

    public bool IsAvailable => State == UpdateState.Available;
    public bool IsUpToDate => State is UpdateState.UpToDate or UpdateState.OptionalInstalled;
    public bool CanAdd => IsOptional && !IsPresent;
    public bool CanRemove => IsOptional && IsPresent;
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
        UpdateState.OptionalInstalled => "Installed",
        UpdateState.OptionalAbsent => "Not installed",
        _ => "Not checked",
    };
}
