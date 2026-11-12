using System.Diagnostics;
using Avalonia.Threading;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed record ConfirmRequest(string Message, string ConfirmText, Command Accept, Command Cancel);

abstract class SectionViewModel : Observable, ISearchable
{
    ConfirmRequest? confirm;
    string? busy;
    bool reloadQueued;

    protected SectionViewModel(ShellViewModel shell, EngineSession session)
    {
        Shell = shell;
        Session = session;
    }

    protected ShellViewModel Shell { get; }
    public EngineSession Session { get; }
    protected string Filter { get; private set; } = "";
    protected bool Rebuilding { get; private set; }

    protected void Replace<T>(System.Collections.ObjectModel.ObservableCollection<T> target, IEnumerable<T> items)
    {
        Rebuilding = true;
        try
        {
            target.Clear();
            foreach (var item in items) target.Add(item);
        }
        finally { Rebuilding = false; }
    }

    public ConfirmRequest? Confirm { get => confirm; private set { if (Set(ref confirm, value)) Raise(nameof(HasConfirm)); } }
    public bool HasConfirm => Confirm is not null;
    public string? Busy { get => busy; private set { if (Set(ref busy, value)) Raise(nameof(IsBusy)); } }
    public bool IsBusy => Busy is not null;

    public void ApplySearch(string text)
    {
        Filter = text;
        ApplyFilter();
    }

    public virtual void OnShown() { }

    protected abstract void ApplyFilter();

    public abstract Task Reload();

    protected static bool Matches(string filter, params string?[] values) =>
        filter.Length == 0 || values.Any(v => v?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true);

    protected void Ask(string message, string confirmText, Func<Task> action) => Confirm = new ConfirmRequest(message, confirmText,
        new Command(async () => { Confirm = null; await action(); }),
        new Command(() => { Confirm = null; return Task.CompletedTask; }));

    protected Task Run(string what, Func<DockerClient, Task> action) =>
        Session.Client is { } client ? Run(what, () => action(client)) : Task.CompletedTask;

    protected async Task Run(string what, Func<Task> action)
    {
        Busy = what;
        try { await action(); }
        catch (Exception ex) { Shell.ShowError(ex.Message); }
        finally { Busy = null; }
    }

    protected void ScheduleReload()
    {
        if (reloadQueued) return;
        reloadQueued = true;
        DispatcherTimer.RunOnce(async () =>
        {
            reloadQueued = false;
            await Reload();
        }, TimeSpan.FromMilliseconds(300));
    }

    protected async Task SafeReload(Func<DockerClient, Task> load)
    {
        if (Session.Client is not { } client || !Session.IsConnected) return;
        try { await load(client); }
        catch (Exception ex) when (ex is DockerException or EngineUnavailableException or HttpRequestException or IOException) { }
    }
}

static class CliRunner
{
    public static async Task<int> Run(IReadOnlyList<string> args, Action<string> onLine)
    {
        var psi = new ProcessStartInfo(Containers.DockerCli.Path())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["COMPOSE_ANSI"] = "never";
        psi.Environment["COMPOSE_PROGRESS"] = "plain";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker");
        void Forward(string? line)
        {
            if (line is not null) Dispatcher.UIThread.Post(() => onLine(line));
        }
        process.OutputDataReceived += (_, e) => Forward(e.Data);
        process.ErrorDataReceived += (_, e) => Forward(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
