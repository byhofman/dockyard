using System.IO.Compression;

namespace Dockyard.Core;

// The dashboard's files next to the tray, tracked in a manifest so that removing it leaves everything else alone.
static class DashboardFiles
{
    public const string ManifestName = "dashboard.files";

    public static void Extract(string zip, string appDir)
    {
        var root = Path.GetFullPath(appDir) + Path.DirectorySeparatorChar;
        var files = new List<string>();
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
        File.WriteAllLines(Path.Combine(appDir, ManifestName), files);
    }

    // Without a manifest (a dashboard from before there was one) only the executable is known to be the dashboard's.
    public static IReadOnlyList<string> Remove(string appDir, string fallbackFile, Action<string> log)
    {
        var root = Path.GetFullPath(appDir) + Path.DirectorySeparatorChar;
        var manifest = Path.Combine(appDir, ManifestName);
        var files = File.Exists(manifest) ? File.ReadAllLines(manifest) : [fallbackFile];
        var removed = new List<string>();
        foreach (var file in files.Where(f => f.Length > 0))
        {
            var target = Path.GetFullPath(Path.Combine(appDir, file));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
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
