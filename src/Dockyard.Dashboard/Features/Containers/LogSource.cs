namespace Dockyard.Dashboard.Features.Containers;

sealed record LogSource(string Id, bool Tty, string? Prefix);
