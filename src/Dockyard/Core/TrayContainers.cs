using Dockyard.Engine;

namespace Dockyard.Core;

sealed record TrayContainer(string Id, string Name, string? Project, bool Running, int? WebPort);

// What the tray menu lists. The menu is rebuilt only when this changes, because a native menu that is replaced while it
// is open closes under the user's pointer.
static class TrayContainers
{
    public const int Limit = 20;

    public static List<TrayContainer> From(IEnumerable<ContainerSummary> containers) => containers
        .Where(c => c.Labels?.ContainsKey("dockyard.helper") != true)
        .Select(c => new TrayContainer(
            c.Id,
            c.Name,
            c.Labels?.GetValueOrDefault("com.docker.compose.project"),
            c.State == "running",
            c.State == "running" ? c.Ports.Where(p => p.Type == "tcp" && p.PublicPort is > 0).OrderBy(p => p.PublicPort).Select(p => p.PublicPort).FirstOrDefault() : null))
        .OrderByDescending(c => c.Running).ThenBy(c => c.Project ?? c.Name, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .Take(Limit)
        .ToList();
}
