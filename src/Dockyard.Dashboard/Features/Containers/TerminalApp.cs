namespace Dockyard.Dashboard.Features.Containers;

sealed record TerminalApp(TerminalKind Kind, string Path)
{
    public static readonly TerminalApp WindowsConsole = new(TerminalKind.WindowsConsole, "docker");
    public static readonly TerminalApp MacTerminal = new(TerminalKind.MacTerminal, "osascript");
}
