using System.IO.Compression;

namespace Dockyard.Core;

// The tray's and the dashboard's files side by side in one folder, each app's tracked in a manifest of its own. Both bring
// the same native libraries, so a file goes only when no other app's manifest still lists it.
static class AppFiles
{
    public const string DashboardManifest = "dashboard.files";
    public const string TrayManifest = "tray.files";
    const string ManifestPattern = "*.files";

    // Whatever the app being replaced installed and this one does not bring goes. A file in use, such as a library the
    // other app has loaded, is moved aside rather than overwritten.
    public static void Extract(string zip, string appDir, string manifestName)
    {
        var root = Root(appDir);
        var files = new List<string>();
        var manifest = Path.Combine(appDir, manifestName);
        var replaced = Listed(manifest);
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
            {
                var target = Path.GetFullPath(Path.Combine(appDir, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                InUseFiles.MoveAside(target);
                entry.ExtractToFile(target);
                files.Add(entry.FullName);
            }
        }
        File.WriteAllLines(manifest, files);
        var kept = OthersList(appDir, manifestName);
        foreach (var file in replaced.Except(files, StringComparer.OrdinalIgnoreCase).Except(kept, StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.GetFullPath(Path.Combine(appDir, file));
            if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) InUseFiles.MoveAside(target);
        }
    }

    public static IReadOnlyList<string> Remove(string appDir, string manifestName, Action<string> log)
    {
        var root = Root(appDir);
        var manifest = Path.Combine(appDir, manifestName);
        var kept = OthersList(appDir, manifestName);
        var removed = new List<string>();
        foreach (var file in Listed(manifest).Except(kept, StringComparer.OrdinalIgnoreCase))
        {
            var target = Path.GetFullPath(Path.Combine(appDir, file));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(target)) continue;
            try
            {
                InUseFiles.MoveAside(target);
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

    static string Root(string appDir) => Path.GetFullPath(appDir) + Path.DirectorySeparatorChar;

    static List<string> Listed(string manifest) => (File.Exists(manifest) ? File.ReadAllLines(manifest) : []).Where(f => f.Length > 0).ToList();

    static HashSet<string> OthersList(string appDir, string manifestName) =>
        (Directory.Exists(appDir) ? Directory.EnumerateFiles(appDir, ManifestPattern) : [])
            .Where(m => !string.Equals(Path.GetFileName(m), manifestName, StringComparison.OrdinalIgnoreCase))
            .SelectMany(Listed)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
