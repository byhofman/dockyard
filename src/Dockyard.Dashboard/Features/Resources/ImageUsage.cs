using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

static class ImageUsage
{
    public static List<ImageRow> Rows(IEnumerable<ImageSummary> images, IEnumerable<ContainerItem> containers)
    {
        var users = containers.GroupBy(c => c.ImageId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.Name).Order().ToList());
        var rows = new List<ImageRow>();
        foreach (var i in images)
        {
            var tags = (i.RepoTags ?? []).Where(t => t != "<none>:<none>").ToList();
            var usedBy = users.GetValueOrDefault(i.Id) ?? [];
            var created = DateTimeOffset.FromUnixTimeSeconds(i.Created);
            if (tags.Count == 0) rows.Add(new ImageRow(i.Id, "<none>", i.Size, created, tags, usedBy));
            else rows.AddRange(tags.Select(t => new ImageRow(i.Id, t, i.Size, created, tags, usedBy)));
        }
        return rows.OrderBy(r => r.IsDangling).ThenBy(r => r.Reference, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
