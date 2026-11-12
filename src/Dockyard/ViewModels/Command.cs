using System.Windows.Input;

namespace Dockyard.ViewModels;

sealed class Command(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public async void Execute(object? parameter) => await run();
    public Task ExecuteAsync() => run();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
