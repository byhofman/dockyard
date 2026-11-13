using System.Windows.Input;

namespace Dockyard.Dashboard.Shell;

sealed class Command(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public async void Execute(object? parameter) => await run();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

sealed class Command<T>(Func<T, Task> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => parameter is T;
    public async void Execute(object? parameter)
    {
        if (parameter is T value) await run(value);
    }
}
