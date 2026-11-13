namespace Dockyard.Dashboard.Features.Containers;

sealed record ProcessCommand(string File, IReadOnlyList<string> Args);
