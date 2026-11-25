using Dockyard.Hosts;

namespace Dockyard.ViewModels;

sealed class ComponentViewModel(ComponentInfo info) : Observable
{
    string installed = "–";
    string latest = "–";
    UpdateState state;
    string? error;
    bool isPresent;
    bool include = true;
    Release? latestRelease;

    public ComponentInfo Info { get; } = info;
    public string Id => Info.Id;
    public string Title => Info.Title;
    public required Command UpdateCommand { get; init; }
    public Command? AddCommand { get; init; }
    public Command? RemoveCommand { get; init; }
    public Release? LatestRelease { get => latestRelease; set { latestRelease = value; Raise(nameof(NotesUrl), nameof(HasNotes)); } }

    public string Installed { get => installed; set => Set(ref installed, value); }
    public string Latest { get => latest; set { if (Set(ref latest, value)) Raise(nameof(UpdateText)); } }
    public string UpdateText => $"Update to {Latest}";
    public string? NotesUrl => UpdateOffers.NotesUrl(Id, LatestRelease);
    public bool HasNotes => NotesUrl is not null && IsAvailable;
    public Command OpenNotesCommand => new(() =>
    {
        if (NotesUrl is { } url)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception) { }
        return Task.CompletedTask;
    });
    public string? Error { get => error; set => Set(ref error, value); }

    public UpdateState State
    {
        get => state;
        set
        {
            if (Set(ref state, value))
                Raise(nameof(StatusText), nameof(IsAvailable), nameof(HasNotes), nameof(IsUpToDate), nameof(IsProblem), nameof(IsNeutral));
        }
    }

    public bool IsPresent { get => isPresent; set { if (Set(ref isPresent, value)) Raise(nameof(CanAdd), nameof(CanRemove)); } }
    // The first install's choice for this row.
    public bool Include { get => include; set => Set(ref include, value); }
    public bool IsOptional => Info.IsOptional;
    public bool IsDockyard => Id is nameof(Core.Component.App) or Core.ComponentRules.DashboardId;
    public bool IsKubernetes => Core.Tools.Standalone.Any(t => t.Id == Id);
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
    public bool IsNeutral => State is UpdateState.Unknown or UpdateState.Checking or UpdateState.CouldNotCheck;

    public string StatusText => State switch
    {
        UpdateState.Checking => "Checking…",
        UpdateState.UpToDate => "Up to date",
        UpdateState.Available => "Update available",
        UpdateState.NotInstalled => "Not installed",
        UpdateState.NoRelease => "No release",
        UpdateState.Error => "Check failed",
        UpdateState.CouldNotCheck => "Could not check right now",
        UpdateState.OptionalInstalled => "Installed",
        UpdateState.OptionalAbsent => "Not installed",
        _ => "Not checked",
    };
}
