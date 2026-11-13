using System.Text.Json;
using System.Text.Json.Serialization;
using Dockyard.Dashboard.Shell;
using Dockyard.Dashboard.Store;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Features.Resources;

sealed record ComposeService(string Name, IReadOnlyList<ContainerItem> Containers)
{
    public int Running => Containers.Count(c => c.IsRunning);
    public string State => Containers.Count == 0 ? "not created" : $"{Running}/{Containers.Count} running";
}

sealed record ComposeProject(string Name, string? WorkingDir, IReadOnlyList<string> ConfigFiles, IReadOnlyList<ComposeService> Services)
{
    public int Running => Services.Sum(s => s.Running);
    public int Total => Services.Sum(s => s.Containers.Count);
    public string Summary => Total == 0 ? "not running" : $"{Running}/{Total} running";
    public string Location => ConfigFiles.Count > 0 ? ConfigFiles[0] : WorkingDir ?? "";
    public bool CanUp => ConfigFiles.Count > 0;
    public bool IsRunning => Running > 0;
}

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

static class ComposeCli
{
    public static string[] Args(string context, ComposeProject project, params string[] verb)
    {
        List<string> args = ["--context", context, "compose", "-p", project.Name];
        foreach (var f in project.ConfigFiles) args.AddRange(["-f", f]);
        if (project.WorkingDir is { Length: > 0 } dir) args.AddRange(["--project-directory", dir]);
        args.AddRange(verb);
        return [.. args];
    }

    public static string DefaultProjectName(string composeFile)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(composeFile))) ?? "project";
        var name = new string(dir.ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        return name.Length > 0 ? name : "project";
    }
}

sealed class RecentCompose
{
    public string Path { get; set; } = "";
    public string Project { get; set; } = "";
}

sealed class DashboardSettings
{
    const int MaxRecent = 10;

    public List<RecentCompose> RecentCompose { get; set; } = [];
    public Dictionary<string, double> PanelSizes { get; set; } = [];

    static DashboardSettings? current;
    public static DashboardSettings Current => current ??= Load();

    public double PanelSize(string key, double fallback) => PanelSizes.TryGetValue(key, out var v) && v > 0 ? v : fallback;

    public void SetPanelSize(string key, double value)
    {
        PanelSizes[key] = Math.Round(value);
        Save();
    }

    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "dashboard.json");

    public static DashboardSettings Load()
    {
        try { return JsonSerializer.Deserialize(System.IO.File.ReadAllText(File), DashboardJson.Default.DashboardSettings) ?? new(); }
        catch (Exception) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, DashboardJson.Default.DashboardSettings));
        }
        catch (IOException) { }
    }

    public void AddRecent(string path, string project)
    {
        RecentCompose.RemoveAll(r => ComposeCatalog.SamePath(r.Path, path));
        RecentCompose.Insert(0, new RecentCompose { Path = path, Project = project });
        if (RecentCompose.Count > MaxRecent) RecentCompose.RemoveRange(MaxRecent, RecentCompose.Count - MaxRecent);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DashboardSettings))]
partial class DashboardJson : JsonSerializerContext;

sealed record ImageRow(string Id, string Reference, long Size, DateTimeOffset Created, IReadOnlyList<string> Tags, IReadOnlyList<string> UsedBy)
{
    public string ShortId => Id.StartsWith("sha256:", StringComparison.Ordinal) ? Id[7..19] : Id[..Math.Min(12, Id.Length)];
    public string SizeText => Format.Bytes(Size);
    public string CreatedText => Format.Age(Created, DateTimeOffset.UtcNow);
    public bool InUse => UsedBy.Count > 0;
    public string UsageText => UsedBy.Count == 0 ? "unused" : UsedBy.Count == 1 ? "1 container" : $"{UsedBy.Count} containers";
    public bool IsDangling => Tags.Count == 0;
}

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

sealed record VolumeRow(string Name, string Driver, string Mountpoint, long? Size, IReadOnlyList<string> UsedBy, IReadOnlyDictionary<string, string> Labels)
{
    public bool IsAnonymous => Name.Length == 64 && Name.All(char.IsAsciiHexDigit);
    public string DisplayName => IsAnonymous ? Name[..12] + "…" : Name;
    public string SizeText => Size is { } s && s >= 0 ? Format.Bytes(s) : "";
    public bool InUse => UsedBy.Count > 0;
    public bool CanRemove => !InUse;
    public string UsageText => UsedBy.Count == 0 ? "unused" : string.Join(", ", UsedBy);
}

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

static class NetworkDrivers
{
    static readonly HashSet<string> BuiltIn = ["bridge", "host", "none", "nat"];

    public static IReadOnlyList<string> For(bool windows) => windows ? ["nat", "transparent", "l2bridge"] : ["bridge", "macvlan", "ipvlan"];

    public static bool IsBuiltIn(string name) => BuiltIn.Contains(name);
}

sealed record DiskCategory(string Name, long Total, long Reclaimable, int Count)
{
    public string TotalText => Format.Bytes(Total);
    public string ReclaimableText => Format.Bytes(Reclaimable);
    public double Fraction => Total > 0 ? (double)Reclaimable / Total : 0;
}

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
