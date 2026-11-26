using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// What one run of the facade reported: its numbered steps as a checklist, every line, and how it ended.
sealed class SetupRun : Observable, IProgress<SetupProgress>
{
    string heading;
    int? step, steps;
    string? error;
    bool isRunning = true, showsDetails;

    public SetupRun(string heading)
    {
        this.heading = heading;
        ToggleDetailsCommand = new Command(() => { ShowsDetails = !ShowsDetails; return Task.CompletedTask; });
    }

    public ObservableCollection<SetupProgress> Lines { get; } = [];
    public ObservableCollection<SetupStep> Checklist { get; } = [];
    public string Heading { get => heading; private set => Set(ref heading, value); }
    public int? Step { get => step; private set => Set(ref step, value); }
    public int? Steps { get => steps; private set => Set(ref steps, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(IsFailed)); } }
    public bool IsFailed => error is not null;
    public bool IsRunning { get => isRunning; private set { if (Set(ref isRunning, value)) Raise(nameof(StatusText)); } }

    // The full log, folded under the checklist until asked for.
    public bool ShowsDetails { get => showsDetails; set { if (Set(ref showsDetails, value)) Raise(nameof(DetailsText)); } }
    public string DetailsText => showsDetails ? "Hide details" : "Show details";
    public Command ToggleDetailsCommand { get; }

    public string? StatusText => Step is { } at && Steps is { } of && of > 1
        ? IsFailed ? $"Stopped at step {at} of {of}" : IsRunning ? $"Step {at} of {of}" : $"{of} of {of} steps"
        : IsFailed ? "Stopped" : null;

    // The bar stands halfway into the current step, so step 1 of 7 does not look like nothing has happened.
    public double Percent => Step is { } at && Steps is { } of && of > 1 ? (at - 0.5) / of * 100 : 0;
    public bool IsIndeterminate => Steps is not > 1;

    SetupStep? Current => Checklist.LastOrDefault(s => s.State is SetupStepState.Running or SetupStepState.Failed);

    public void Report(SetupProgress value)
    {
        if (value.Step is { } at)
        {
            Heading = value.Line;
            Step = at;
            Steps = value.Steps;
            Raise(nameof(StatusText), nameof(Percent), nameof(IsIndeterminate));
            if (value.Plan is { } plan && Checklist.Count == 0)
                foreach (var title in plan) Checklist.Add(new SetupStep(title));
            while (Checklist.Count < at) Checklist.Add(new SetupStep(Checklist.Count == at - 1 ? value.Line : ""));
            for (var i = 0; i < at - 1; i++) Checklist[i].State = SetupStepState.Done;
            Checklist[at - 1].State = SetupStepState.Running;
        }
        else if (Current is { } current)
        {
            current.Lines.Add(value);
            if (value.Kind is SetupProgressKind.Info) current.Latest = value.Line;
        }
        if (value.Kind == SetupProgressKind.Error)
        {
            Error = value.Line;
            Fail();
            Raise(nameof(StatusText));
        }
        // A run of one step has its line as the heading already.
        if (value.Steps is not 1) Lines.Add(value);
    }

    public void Finish(string? failure = null)
    {
        if (failure is not null && Error is null) Report(new SetupProgress(failure, SetupProgressKind.Error));
        if (Error is null)
            foreach (var s in Checklist) s.State = SetupStepState.Done;
        IsRunning = false;
        Raise(nameof(StatusText));
    }

    // The step that failed opens with its own lines; the rest stays as it was.
    void Fail()
    {
        if (Current is not { } failed) return;
        failed.State = SetupStepState.Failed;
        failed.IsExpanded = true;
    }

    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Line));
}

enum SetupStepState { ToDo, Running, Done, Failed }

sealed class SetupStep(string title) : Observable
{
    SetupStepState state;
    bool isExpanded;

    public string Title { get; } = title;
    public ObservableCollection<SetupProgress> Lines { get; } = [];

    public SetupStepState State
    {
        get => state;
        set { if (Set(ref state, value)) Raise(nameof(IsToDo), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(StateText), nameof(ShowsLatest)); }
    }

    public bool IsToDo => state == SetupStepState.ToDo;
    public bool IsRunning => state == SetupStepState.Running;
    public bool IsDone => state == SetupStepState.Done;
    public bool IsFailed => state == SetupStepState.Failed;
    // For screen readers, which see no icon.
    public string StateText => state switch
    {
        SetupStepState.Done => "Done",
        SetupStepState.Running => "Running",
        SetupStepState.Failed => "Failed",
        _ => "To do",
    };

    public bool IsExpanded { get => isExpanded; set => Set(ref isExpanded, value); }

    // What the running step is doing now, under its title, so a long step does not look stuck.
    public string? Latest { get => latest; set { if (Set(ref latest, value)) Raise(nameof(ShowsLatest)); } }
    public bool ShowsLatest => IsRunning && latest is not null;
    string? latest;
}
