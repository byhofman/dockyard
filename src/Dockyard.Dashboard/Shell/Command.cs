using System.Windows.Input;

namespace Dockyard.Dashboard.Shell;

sealed class Command(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    // A command runs from a button: what escapes it would end the process from async void.
    public async void Execute(object? parameter)
    {
        try { await run(); }
        catch (Exception ex) { Failures.Report(ex, "command"); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

sealed class Command<T>(Func<T, Task> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T;
    public async void Execute(object? parameter)
    {
        if (parameter is not T value) return;
        try { await run(value); }
        catch (Exception ex) { Failures.Report(ex, "command"); }
    }
}
