namespace Dockyard.Core;

sealed record ShellResult(int ExitCode, string Output, string Error);
