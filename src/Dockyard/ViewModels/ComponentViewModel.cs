using Dockyard.Hosts;
using Dockyard.Setup;

namespace Dockyard.ViewModels;

// Flag: its name for the command line, which an optional component has.
sealed class ComponentViewModel(ComponentRow row, string? flag) : Observable
{
    string installed = "–";
    string latest = "–";
    UpdateState state;
    string? error;
    bool isPresent;
    bool include = true;
    Release? latestRelease;

    public ComponentRow Row { get; } = row;
    public string? Flag { get; } = flag;
    public string Id => Row.Id;
    public string Title => Row.Title;
    public required Command UpdateCommand { get; init; }
    public Command? AddCommand { get; init; }
    public Command? RemoveCommand { get; init; }
    public Release? LatestRelease { get => latestRelease; set { latestRelease = value; Raise(nameof(NotesUrl), nameof(HasNotes)); } }

    public string Installed { get => installed; set => Set(ref installed, value); }
    string? source;
    // Where the installed version came from: winget, Homebrew, apt, dnf, or Dockyard itself.
    public string? Source { get => source; set { if (Set(ref source, value)) Raise(nameof(HasSource)); } }
    public bool HasSource => source is not null;
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
    public bool IsOptional => Flag is not null;
    public bool IsDockyard => Row.Group == "Dockyard";
    public bool IsApp => Id is nameof(Core.Component.Tray);
    public bool IsKubernetes => Row.Group == "Kubernetes";
    public string AddId => "Add-" + Id;
    public string RemoveId => "Remove-" + Id;
    public string UpdateId => "Update-" + Id;
    public string Note => Row.RequiredBecause is { } reason ? "Required: " + reason : Row.Needs ?? "";
    public bool HasNote => Note.Length > 0;
    public string IncludeText => $"{Title}" + (Row.Needs is { } note ? $". {note}" : "");

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
