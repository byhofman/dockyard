using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// What one run of the facade reported: its numbered steps, every line, and how it ended.
sealed class SetupRun : Observable, IProgress<SetupProgress>
{
    string heading;
    int? step, steps;
    string? error;
    bool isRunning = true;

    public SetupRun(string heading) => this.heading = heading;

    public ObservableCollection<SetupProgress> Lines { get; } = [];
    public string Heading { get => heading; private set => Set(ref heading, value); }
    public int? Step { get => step; private set => Set(ref step, value); }
    public int? Steps { get => steps; private set => Set(ref steps, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(IsFailed)); } }
    public bool IsFailed => error is not null;
    public bool IsRunning { get => isRunning; private set => Set(ref isRunning, value); }

    public void Report(SetupProgress value)
    {
        if (value.Step is { } at)
        {
            Heading = value.Line;
            Step = at;
            Steps = value.Steps;
        }
        if (value.Kind == SetupProgressKind.Error) Error = value.Line;
        // A run of one step has its line as the heading already.
        if (value.Steps is not 1) Lines.Add(value);
    }

    public void Finish(string? failure = null)
    {
        if (failure is not null && Error is null) Report(new SetupProgress(failure, SetupProgressKind.Error));
        IsRunning = false;
    }

    public string Text => string.Join(Environment.NewLine, Lines.Select(l => l.Line));
}
