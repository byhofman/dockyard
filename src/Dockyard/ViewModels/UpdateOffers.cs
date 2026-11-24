using Dockyard.Core;

namespace Dockyard.ViewModels;

static class UpdateOffers
{
    // What the tray offers, naming the version it goes to; Dockyard's own update leads.
    public static string? Text(IReadOnlyList<(string Id, string Title, string Latest)> available)
    {
        if (available.Count == 0) return null;
        if (available.FirstOrDefault(a => a.Id == nameof(Component.App)) is { Id: not null } app)
            return available.Count == 1 ? $"Update Dockyard to {app.Latest}" : $"Update Dockyard to {app.Latest} and {available.Count - 1} more";
        return available.Count == 1 ? $"Update {available[0].Title} to {available[0].Latest}" : $"Install {available.Count} updates";
    }

    public static string? NotesUrl(string id, Hosts.Release? release) =>
        release is not null && id is nameof(Component.App) or ComponentRules.DashboardId
            ? $"https://github.com/{Paths.Repo}/releases/tag/{release.Tag}"
            : null;
}
