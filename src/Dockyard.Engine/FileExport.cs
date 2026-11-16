using System.Formats.Tar;

namespace Dockyard.Engine;

public sealed record ExportResult(int Files, long Bytes, int Skipped);

// A folder from the archive API written out as plain files and folders under a new folder on the host. Only those two
// kinds are written, and only inside the destination: links, devices and any name that would climb out are counted as
// skipped, so nothing in a volume can make the host write elsewhere or point at its files.
public static class FileExport
{
    public static async Task<ExportResult> ExtractAsync(Stream tar, string destination, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(destination);
        var inside = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var invalid = Path.GetInvalidFileNameChars();
        int files = 0, skipped = 0;
        long bytes = 0;
        Directory.CreateDirectory(root);
        await using var reader = new TarReader(tar);
        while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
        {
            // The archive of /volume names everything volume/…; the folder itself is the destination.
            var parts = entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").Skip(1).ToArray();
            if (parts.Length == 0) continue;
            if (parts.Any(p => p == ".." || p.IndexOfAny(invalid) >= 0)) { skipped++; continue; }
            var target = Path.GetFullPath(Path.Combine([root, .. parts]));
            if (!target.StartsWith(inside, comparison)) { skipped++; continue; }
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(target);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                        if (entry.DataStream is not null) await entry.DataStream.CopyToAsync(file, ct);
                    files++;
                    bytes += entry.Length;
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        return new ExportResult(files, bytes, skipped);
    }
}
