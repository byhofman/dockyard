using Dockyard.Core;

namespace Dockyard.Setup;

// With and Without name components by flag or id. Neither: a new install gets everything, an existing one keeps what it has.
// Source null keeps what an earlier install chose, and KubectlShell false leaves the shell setting as it is. Silent: nothing
// may pop up.
public sealed record SetupRequest(IReadOnlyList<string> With, IReadOnlyList<string> Without, ToolSource? Source, bool TrustHttps, bool KubectlShell, bool Silent, bool Resume)
{
    // Start Dockyard afterwards.
    public bool Start { get; init; } = true;
    // Windows: the Linux engine image to import instead of downloading it.
    public string? Image { get; init; }
    // What Without removes from an existing install takes its images and containers along.
    public bool RemoveData { get; init; }
}
