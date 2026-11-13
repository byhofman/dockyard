namespace Dockyard.Engine;

public sealed record ImportSummary(int Images, int Volumes, int Containers, int Networks, IReadOnlyList<string> Skipped);
