using System.IO.Compression;

namespace Dockyard.Core;

// The dashboard's files next to the tray, tracked in a manifest so that removing it leaves everything else alone.
static class DashboardFiles
{
    public const string ManifestName = "dashboard.files";

    // Whatever an earlier dashboard installed and this one does not bring goes, so that a new name leaves no old exe behind.
    public static void Extract(string zip, string appDir, params string[] alsoRemove)
    {
        var root = Path.GetFullPath(appDir) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var manifest = Path.Combine(appDir, ManifestName);
        var earlier = (File.Exists(manifest) ? File.ReadAllLines(manifest) : []).Concat(alsoRemove).Where(f => f.Length > 0).ToList();
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(appDir, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                files.Add(entry.FullName);
            }
        }
        File.WriteAllLines(manifest, files);
        foreach (var file in earlier.Except(files, StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.GetFullPath(Path.Combine(appDir, file));
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) InUseFiles.MoveAside(target);
        }
    }

    // Without a manifest (a dashboard from before there was one) only the executables are known to be the dashboard's.
    public static IReadOnlyList<string> Remove(string appDir, IReadOnlyList<string> always, Action<string> log)
    {
        var root = Path.GetFullPath(appDir) + Path.DirectorySeparatorChar;
        var manifest = Path.Combine(appDir, ManifestName);
        var files = (File.Exists(manifest) ? File.ReadAllLines(manifest) : []).Concat(always).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var removed = new List<string>();
        foreach (var file in files.Where(f => f.Length > 0))
        {
            var target = Path.GetFullPath(Path.Combine(appDir, file));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(target)) continue;
            try
            {
                File.Delete(target);
                removed.Add(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"  in use, left behind: {target}");
            }
        }
        File.Delete(manifest);
        return removed;
    }
}
