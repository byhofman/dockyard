using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Resources;

static class ComposeCatalog
{
    public const string WorkingDirLabel = "com.docker.compose.project.working_dir";
    public const string ConfigFilesLabel = "com.docker.compose.project.config_files";

    public static List<ComposeProject> Build(IEnumerable<ContainerItem> containers, IEnumerable<RecentCompose> recents)
    {
        var projects = containers.Where(c => c.Project.Length > 0)
            .GroupBy(c => c.Project, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var files = (first.Labels.GetValueOrDefault(ConfigFilesLabel)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
                    .Where(f => IsLocal(f) && File.Exists(f)).ToArray();
                var workingDir = first.Labels.GetValueOrDefault(WorkingDirLabel) is { } dir && IsLocal(dir) && Directory.Exists(dir) ? dir : null;
                var services = g.GroupBy(c => c.Service)
                    .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(s => new ComposeService(s.Key, s.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList()))
                    .ToList();
                return new ComposeProject(g.Key, workingDir, files, services);
            })
            .ToList();

        foreach (var recent in recents)
        {
            var match = projects.Any(p => p.Name == recent.Project || p.ConfigFiles.Any(f => SamePath(f, recent.Path)));
            if (!match) projects.Add(new ComposeProject(recent.Project, Path.GetDirectoryName(recent.Path), [recent.Path], []));
        }
        return projects.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Labels are whatever the image or whoever started the container wrote. Up and pull run compose with these files, so
    // only ones on this machine count: a network path would have Windows hand the user's credentials to its server.
    static bool IsLocal(string path) =>
        Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal);

    public static bool SamePath(string a, string b) => string.Equals(
        Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
}
