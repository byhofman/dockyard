using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

static class VolumeRows
{
    public static List<VolumeRow> Rows(IEnumerable<Volume> volumes, IEnumerable<ContainerItem> containers, IReadOnlyDictionary<string, long>? sizes)
    {
        var users = containers.SelectMany(c => c.VolumeNames.Select(v => (Volume: v, c.Name)))
            .GroupBy(x => x.Volume)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).Distinct().Order().ToList());
        return volumes
            .Select(v => new VolumeRow(v.Name, v.Driver, v.Mountpoint, sizes?.GetValueOrDefault(v.Name, -1) is { } s and >= 0 ? s : null,
                users.GetValueOrDefault(v.Name) ?? [], v.Labels ?? []))
            .OrderBy(r => r.IsAnonymous).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
