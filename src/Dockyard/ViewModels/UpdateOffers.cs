using Dockyard.Core;

namespace Dockyard.ViewModels;

static class UpdateOffers
{
    // What the tray offers, naming the version it goes to; Dockyard's own update leads.
    public static string? Text(IReadOnlyList<(string Id, string Title, string Latest)> available, string selfId)
    {
        if (available.Count == 0) return null;
        if (available.FirstOrDefault(a => a.Id == selfId) is { Id: not null } app)
            return available.Count == 1 ? $"Update Dockyard to {app.Latest}" : $"Update Dockyard to {app.Latest} and {available.Count - 1} more";
        return available.Count == 1 ? $"Update {available[0].Title} to {available[0].Latest}" : $"Install {available.Count} updates";
    }

    public static string? NotesUrl(string id, Hosts.Release? release, string selfId) =>
        release is not null && (id == selfId || id == ComponentRules.DashboardId)
            ? $"https://github.com/{Paths.Repo}/releases/tag/{release.Tag}"
            : null;
}
