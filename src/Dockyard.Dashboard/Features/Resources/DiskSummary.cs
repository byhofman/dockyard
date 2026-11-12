using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

static class DiskSummary
{
    public static List<DiskCategory> From(SystemDf df, IEnumerable<ContainerItem> containers)
    {
        var usedImages = containers.Select(c => c.ImageId).ToHashSet();
        var images = df.Images ?? [];
        var dfContainers = df.Containers ?? [];
        var volumes = df.Volumes ?? [];
        var cache = df.BuildCache ?? [];
        return
        [
            new("Images", images.Sum(i => i.Size), images.Where(i => !usedImages.Contains(i.Id)).Sum(i => i.Size), images.Count),
            new("Containers", dfContainers.Sum(c => c.SizeRw), dfContainers.Where(c => c.State != "running").Sum(c => c.SizeRw), dfContainers.Count),
            new("Volumes", volumes.Sum(v => Math.Max(0, v.UsageData?.Size ?? 0)),
                volumes.Where(v => v.UsageData?.RefCount == 0).Sum(v => Math.Max(0, v.UsageData?.Size ?? 0)), volumes.Count),
            new("Build cache", cache.Sum(b => b.Size), cache.Where(b => !b.InUse).Sum(b => b.Size), cache.Count),
        ];
    }
}
