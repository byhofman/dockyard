namespace Dockyard.Setup;

// Step and Steps number the steps of a longer run, such as "[2/5]"; the lines between them have neither.
public sealed record SetupProgress(string Line, SetupProgressKind Kind = SetupProgressKind.Info, int? Step = null, int? Steps = null);

public enum SetupProgressKind { Info, Command, Warning, Error }

// Reports as it happens, on the caller's thread, which System.Progress does not.
internal sealed class ReportTo(Action<SetupProgress> report) : IProgress<SetupProgress>
{
    public void Report(SetupProgress value) => report(value);
}
