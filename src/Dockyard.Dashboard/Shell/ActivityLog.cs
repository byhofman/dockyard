using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace Dockyard.Dashboard.Shell;

// What the dashboard does, out of the way: the footer shows the step at hand, and a click on it shows the rest.
sealed class ActivityLog : Observable
{
    const int Kept = 500;
    string current = "Ready";
    int running;
    bool isOpen;

    public ActivityLog() => ToggleCommand = new Command(() => { IsOpen = !IsOpen; return Task.CompletedTask; });

    public ObservableCollection<string> Lines { get; } = [];
    public Command ToggleCommand { get; }
    public string Current { get => current; private set => Set(ref current, value); }
    public bool IsWorking => running > 0;
    public bool IsOpen { get => isOpen; set => Set(ref isOpen, value); }

    public void Note(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Note(line));
            return;
        }
        line = line.Trim();
        if (line.Length == 0) return;
        Lines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Lines.Count > Kept) Lines.RemoveAt(0);
        Current = line;
    }

    // Until it is disposed, the footer shows that something runs.
    public IDisposable Working(string what)
    {
        Note(what);
        running++;
        Raise(nameof(IsWorking));
        return new Done(() =>
        {
            running--;
            Raise(nameof(IsWorking));
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
