using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Dockyard.Dashboard.Shell;

enum ActivityKind { Step, Detail, Command, Info, Error }

sealed record ActivityEntry(DateTime Time, string Text, ActivityKind Kind)
{
    public string TimeText => Time.ToString("HH:mm:ss");
    public bool IsStep => Kind == ActivityKind.Step;
    public bool IsDetail => Kind == ActivityKind.Detail;
    public bool IsCommand => Kind == ActivityKind.Command;
    public bool IsInfo => Kind == ActivityKind.Info;
    public bool IsError => Kind == ActivityKind.Error;
    public override string ToString() => $"{TimeText}  {(Kind == ActivityKind.Detail ? "  " : "")}{Text}";
}

// What the dashboard does, out of the way: the footer shows the step at hand, and a click on it shows the rest.
sealed class ActivityLog : Observable
{
    const int Kept = 500;
    string current = "Ready";
    bool currentIsError;
    int running;
    bool isOpen;

    public ActivityLog()
    {
        ToggleCommand = new Command(() => { IsOpen = !IsOpen; return Task.CompletedTask; });
        ClearCommand = new Command(() =>
        {
            Lines.Clear();
            Raise(nameof(HasLines));
            return Task.CompletedTask;
        });
    }

    public ObservableCollection<ActivityEntry> Lines { get; } = [];
    public bool HasLines => Lines.Count > 0;
    public Command ToggleCommand { get; }
    public Command ClearCommand { get; }
    public string Current { get => current; private set => Set(ref current, value); }
    public bool CurrentIsError { get => currentIsError; private set => Set(ref currentIsError, value); }
    public bool IsWorking => running > 0;
    public bool IsOpen { get => isOpen; set => Set(ref isOpen, value); }
    public string Text => string.Join(Environment.NewLine, Lines);

    public void Note(string line, ActivityKind kind = ActivityKind.Detail)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Note(line, kind));
            return;
        }
        line = line.Trim();
        if (line.Length == 0) return;
        Lines.Add(new ActivityEntry(DateTime.Now, line, kind));
        if (Lines.Count == 1) Raise(nameof(HasLines));
        while (Lines.Count > Kept) Lines.RemoveAt(0);
        if (kind == ActivityKind.Command) return;
        Current = line;
        CurrentIsError = kind == ActivityKind.Error;
    }

    // Until it is disposed, the footer shows that something runs.
    public IDisposable Working(string what)
    {
        Note(what, ActivityKind.Step);
        running++;
        Raise(nameof(IsWorking));
        return new Done(() =>
        {
            running--;
            Raise(nameof(IsWorking));
            if (running == 0 && Lines.LastOrDefault()?.Kind is not (ActivityKind.Info or ActivityKind.Error)) Current = "Ready";
        });
    }

    sealed class Done(Action done) : IDisposable
    {
        bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Dispatcher.UIThread.CheckAccess()) done();
            else Dispatcher.UIThread.Post(done);
        }
    }
}
