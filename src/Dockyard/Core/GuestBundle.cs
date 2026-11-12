using System.Formats.Tar;
using System.IO.Compression;

namespace Dockyard.Core;

static class GuestBundle
{
    public const string ResourceName = "guest.tar.gz";

    // The agent out of the bundle Dockyard carries; null for a build without one.
    public static async Task<string?> ExtractAgent(string directory)
    {
        await using var resource = typeof(GuestBundle).Assembly.GetManifestResourceStream(ResourceName);
        if (resource is null) return null;
        await using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (!entry.Name.EndsWith("/dockyard-agent", StringComparison.Ordinal) || entry.DataStream is null) continue;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "dockyard-agent");
            await using var file = File.Create(path);
            await entry.DataStream.CopyToAsync(file);
            return path;
        }
        return null;
    }
}
