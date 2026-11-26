using Dockyard.Setup;
using Spectre.Console;

namespace Dockyard.Cli;

// Steps as "[2/5] Adding kind", errors on stderr. Reports as it happens: System.Progress would hand over on another thread.
sealed class ProgressRenderer(IAnsiConsole console, TextWriter error) : IProgress<SetupProgress>
{
    public void Report(SetupProgress progress)
    {
        switch (progress.Kind)
        {
            case SetupProgressKind.Error:
                error.WriteLine(progress.Line);
                break;
            case SetupProgressKind.Warning:
                console.WriteLine("warning: " + progress.Line);
                break;
            default:
                console.WriteLine(progress.Step is { } step && progress.Steps is { } steps ? $"[{step}/{steps}] {progress.Line}" : progress.Line);
                break;
        }
    }
}
