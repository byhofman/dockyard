using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dockyard.Core;

enum Component { Engine, Compose, Buildx, WslEngine, App }

sealed record Release(Version Version, string Tag, string? DownloadUrl);

static partial class Updates
{
    public const string StaticIndex = "https://download.docker.com/win/static/stable/x86_64/";

    // Defender ASR's prevalence/age rule blocks freshly published binaries.
    static readonly TimeSpan MinAge = TimeSpan.FromDays(14);

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dockyard");
        return http;
    }

    public static string Title(Component c) => c switch
    {
        Component.Engine => "Docker Engine & CLI (Windows)",
        Component.Compose => "Docker Compose",
        Component.Buildx => "Docker Buildx",
        Component.WslEngine => "Docker CE (WSL)",
        Component.App => "Dockyard",
        _ => c.ToString(),
    };

    public static bool NeedsElevation(Component c) => c is not Component.WslEngine;

    public static async Task<Version?> Installed(Component c, string distro) => c switch
    {
        Component.Engine => File.Exists(Paths.DockerdExe) ? ParseVersion(await Shell.Check(Paths.DockerdExe, "--version")) : null,
        Component.Compose => await PluginVersion("docker-compose.exe", "version", "--short"),
        Component.Buildx => await PluginVersion("docker-buildx.exe", "version"),
        Component.WslEngine => ParseVersion((await Shell.Run("wsl.exe", "-d", distro, "-e", "dpkg-query", "-W", "-f=${Version}", "docker-ce")).Output),
        Component.App => ParseVersion(Paths.AppVersion),
        _ => null,
    };

    public static async Task<Release?> Latest(Component c, string distro) => c switch
    {
        Component.Engine => await LatestStatic(),
        Component.Compose => await LatestGitHub("docker/compose", @"^docker-compose-windows-x86_64\.exe$"),
        Component.Buildx => await LatestGitHub("docker/buildx", @"^buildx-v[\d.]+\.windows-amd64\.exe$"),
        Component.WslEngine => await LatestApt(distro),
        Component.App => await LatestApp(),
        _ => null,
    };

    static async Task<Version?> PluginVersion(string exe, params string[] args)
    {
        var path = Path.Combine(Paths.PluginDir, exe);
        return File.Exists(path) ? ParseVersion((await Shell.Run(path, args)).Output) : null;
    }

    public static async Task<Release> LatestStatic()
    {
        var index = await Http.GetStringAsync(StaticIndex);
        var candidates = StaticEntry().Matches(index)
            .Select(m => (Version: Version.Parse(m.Groups["v"].Value), Date: ParseDate(m.Groups["d"].Value)))
            .ToList();
        var eligible = candidates.Where(x => x.Date is null || DateTime.UtcNow - x.Date > MinAge).ToList();
        var best = (eligible.Count > 0 ? eligible : candidates).MaxBy(x => x.Version);
        if (best.Version is null) throw new InvalidOperationException("No Docker static release found");
        var name = $"docker-{best.Version}.zip";
        return new Release(best.Version, best.Version.ToString(), StaticIndex + name);
    }

    public static async Task<Release> LatestGitHub(string repo, string assetPattern)
    {
        var releases = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=20"))!.AsArray();
        foreach (var r in releases)
        {
            if (r is null || r["prerelease"]!.GetValue<bool>() || r["draft"]!.GetValue<bool>()) continue;
            if (DateTime.UtcNow - r["published_at"]!.GetValue<DateTime>() < MinAge) continue;
            var asset = r["assets"]!.AsArray().FirstOrDefault(a => Regex.IsMatch(a!["name"]!.GetValue<string>(), assetPattern));
            if (asset is null) continue;
            var tag = r["tag_name"]!.GetValue<string>();
            return new Release(ParseVersion(tag)!, tag, asset["browser_download_url"]!.GetValue<string>());
        }
        throw new InvalidOperationException($"No suitable {repo} release found");
    }

    static async Task<Release?> LatestApt(string distro)
    {
        await Shell.Check("wsl.exe", "-d", distro, "-u", "root", "--", "apt-get", "update", "-qq",
            "-o", "Dir::Etc::sourcelist=sources.list.d/docker.list", "-o", "Dir::Etc::sourceparts=-", "-o", "APT::Get::List-Cleanup=0");
        var policy = await Shell.Check("wsl.exe", "-d", distro, "--", "apt-cache", "policy", "docker-ce");
        var candidate = AptCandidate().Match(policy);
        var version = candidate.Success ? ParseVersion(candidate.Groups[1].Value) : null;
        return version is null ? null : new Release(version, candidate.Groups[1].Value, null);
    }

    static async Task<Release?> LatestApp()
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Paths.Repo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var tag = release["tag_name"]!.GetValue<string>();
        var asset = release["assets"]!.AsArray().FirstOrDefault(a => a!["name"]!.GetValue<string>() == "Dockyard.exe");
        return ParseVersion(tag) is { } v && asset is not null
            ? new Release(v, tag, asset["browser_download_url"]!.GetValue<string>())
            : null;
    }

    public static async Task<string> DownloadApp(Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "Dockyard.exe");
        await DownloadFile(release.DownloadUrl!, exe);
        return exe;
    }

    public static async Task DownloadFile(string url, string target)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var file = File.Create(target);
        await response.Content.CopyToAsync(file);
    }

    public static Version? ParseVersion(string? text)
    {
        var m = text is null ? null : VersionNumber().Match(text);
        return m is { Success: true } ? Version.Parse(m.Value) : null;
    }

    static DateTime? ParseDate(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;

    [GeneratedRegex(@"docker-(?<v>\d+\.\d+\.\d+)\.zip</a>\s*(?<d>\d{4}-\d{2}-\d{2} \d{2}:\d{2})?")]
    private static partial Regex StaticEntry();

    [GeneratedRegex(@"Candidate:\s*(\S+)")]
    private static partial Regex AptCandidate();

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();
}
