using Dockyard.Setup;

namespace Dockyard.Dashboard;

// What the dashboard's command-line actions use of the facade.
interface IHeadlessSetup : IDisposable
{
    Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct);
    Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress);
    Task Add(string id, IProgress<SetupProgress> progress);
    Task Remove(string id, bool removeData, IProgress<SetupProgress> progress);
    Task Uninstall(bool removeData, IProgress<SetupProgress> progress, bool silent);
}

sealed class HeadlessSetup(DockyardSetup setup) : IHeadlessSetup
{
    public Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct) => setup.Setup(request, progress, ct);
    public Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress) => setup.Update(ids, progress);
    public Task Add(string id, IProgress<SetupProgress> progress) => setup.Add(id, progress);
    public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress) => setup.Remove(id, removeData, progress);
    public Task Uninstall(bool removeData, IProgress<SetupProgress> progress, bool silent) => setup.Uninstall(removeData, progress, silent);
    public void Dispose() => setup.Dispose();
}

// What Program.Run starts from; the tests bring their own.
sealed record Launch(
    Func<string[], bool> IsWorker,
    Func<string[], int> Worker,
    Func<IHeadlessSetup> Setup,
    TextWriter Out,
    TextWriter Error,
    bool HasDesktop,
    Func<bool> HasTerminal,
    Action AttachConsole,
    Func<string?, int> Window)
{
    // The console is read only once it is attached: a Windows app that looks at it before has none, and keeps none.
    public static Launch ForThisMachine(Func<string?, int>? window = null) => new(
        DockyardSetup.IsElevatedWorker,
        DockyardSetup.RunElevatedWorker,
        () => new HeadlessSetup(DockyardSetup.ForCommandLine()),
        new LateWriter(() => Console.Out),
        new LateWriter(() => Console.Error),
        DockyardSetup.HasDesktop,
        () => DockyardSetup.HasTerminal,
        DockyardSetup.AttachParentConsole,
        window ?? (_ => 0));

    sealed class LateWriter(Func<TextWriter> target) : TextWriter
    {
        public override System.Text.Encoding Encoding => target().Encoding;
        public override void Write(char value) => target().Write(value);
        public override void Write(string? value) => target().Write(value);
        public override void WriteLine(string? value) => target().WriteLine(value);
    }
}

// The dashboard's own install, update and uninstall without a window, for winget, Homebrew and the steps the dashboard hands
// over to a copy of itself. Exit codes are the installer's.
static class Headless
{
    public static int Run(AppCommand command, IReadOnlyList<string> problems, Launch launch)
    {
        if (problems.Count > 0)
        {
            foreach (var problem in problems) launch.Error.WriteLine(problem);
            launch.Error.WriteLine("dockyard-dashboard --setup --silent takes the options of dockyard setup; --update [component…], --component add|remove <component> [--remove-data] and --uninstall [--remove-data] those of their dockyard commands.");
            return DockyardSetup.WrongArguments;
        }
        if (command.Setup.Assets is { } assets) DockyardSetup.LocalAssets = assets;
        var progress = new Lines(launch.Out, launch.Error);
        try
        {
            using var setup = launch.Setup();
            switch (command.Action)
            {
                case AppAction.Setup:
                    return setup.Setup(command.Setup with { Headless = !launch.HasDesktop }, progress, CancellationToken.None).GetAwaiter().GetResult();
                case AppAction.Update:
                    setup.Update(command.Components.Count == 0 ? null : command.Components, progress).GetAwaiter().GetResult();
                    break;
                case AppAction.Add:
                    setup.Add(command.Components[0], progress).GetAwaiter().GetResult();
                    break;
                case AppAction.Remove:
                    setup.Remove(command.Components[0], command.RemoveData, progress).GetAwaiter().GetResult();
                    break;
                default:
                    setup.Uninstall(command.RemoveData, progress, silent: !launch.HasTerminal()).GetAwaiter().GetResult();
                    break;
            }
            return DockyardSetup.Ok;
        }
        catch (ArgumentException e)
        {
            launch.Error.WriteLine(e.Message);
            return DockyardSetup.WrongArguments;
        }
        // Whatever else went wrong, as a network, a process or a file can: its message, never a stack trace.
        catch (Exception e)
        {
            launch.Error.WriteLine(e.Message);
            return DockyardSetup.Failed;
        }
    }

    sealed class Lines(TextWriter output, TextWriter error) : IProgress<SetupProgress>
    {
        public void Report(SetupProgress value)
        {
            var line = value.Step is { } step && value.Steps is { } steps ? $"[{step}/{steps}] {value.Line}" : value.Line;
            (value.Kind is SetupProgressKind.Warning or SetupProgressKind.Error ? error : output).WriteLine(line);
        }
    }
}
