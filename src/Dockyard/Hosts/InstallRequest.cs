namespace Dockyard.Hosts;

// Silent: nothing may pop up, so a step that would need a prompt uses what works without one, or says what was skipped.
sealed record InstallRequest(bool Dashboard, string? Image = null, bool Silent = false, bool TrustHttps = false)
{
    // Ids from the catalog that stay out, or go if they are there. Empty for almost everyone, and then setup is what it always was.
    public IReadOnlyCollection<string> Without { get; init; } = [];
}
