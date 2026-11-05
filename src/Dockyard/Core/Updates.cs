using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dockyard.Hosts;

namespace Dockyard.Core;

enum Component { Engine, Compose, Buildx, LinuxEngine, App }

// Offered by the wizard and the tray window next to the components that always come with Dockyard.
static class OptionalComponents
{
    public const string Dashboard = "Dashboard";
}

static partial class Updates
{
    public const string StaticIndex = "https://download.docker.com/win/static/stable/x86_64/";
    public const string LinuxStaticIndex = "https://download.docker.com/linux/static/stable/x86_64/";
    public const string LinuxComposeAsset = @"^docker-compose-linux-x86_64$";
    public const string LinuxBuildxAsset = @"^buildx-v[\d.]+\.linux-amd64$";
    public const string ImageAsset = @"^dockyard-linux-[\d.]+-x86_64\.tar\.gz$";

    // Defender ASR's prevalence/age rule blocks freshly published binaries.
    static readonly TimeSpan MinAge = TimeSpan.FromDays(14);

    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dockyard");
        return http;
    }

    public static string Title(Component c) => c switch
    {
        Component.Engine => "Docker Engine & CLI (Windows)",
        Component.Compose => "Docker Compose",
        Component.Buildx => "Docker Buildx",
        Component.LinuxEngine => "Docker Engine (Linux, WSL)",
        Component.App => "Dockyard",
        _ => c.ToString(),
    };

    [SupportedOSPlatform("windows")]
    public static async Task<Version?> Installed(Component c) => c switch
    {
        Component.Engine => File.Exists(Paths.DockerdExe) ? ParseVersion(await Shell.Check(Paths.DockerdExe, "--version")) : null,
        Component.Compose => await PluginVersion("docker-compose.exe", "version", "--short"),
        Component.Buildx => await PluginVersion("docker-buildx.exe", "version"),
        Component.LinuxEngine => await new WslDistro(ProcessRunner.Instance).DockerVersion(),
        // The wizard runs from elsewhere and can be newer than what is installed, so not this process's own version.
        Component.App => File.Exists(Paths.AppExe) ? ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(Paths.AppExe).ProductVersion) : null,
        _ => null,
    };

    public static async Task<Release?> Latest(Component c) => c switch
    {
        Component.Engine => await LatestStatic(),
        Component.Compose => await LatestGitHub("docker/compose", @"^docker-compose-windows-x86_64\.exe$"),
        Component.Buildx => await LatestGitHub("docker/buildx", @"^buildx-v[\d.]+\.windows-amd64\.exe$"),
        Component.LinuxEngine => await LatestStatic(LinuxStaticIndex, "tgz"),
        Component.App => await LatestApp(),
        _ => null,
    };

    // GitHub allows 60 anonymous API calls an hour per address, too few on shared CI runners.
    static HttpRequestMessage GitHubApi(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (Environment.GetEnvironmentVariable("DOCKYARD_GITHUB_TOKEN") is { Length: > 0 } token)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    static async Task<string> GetGitHubApi(string url)
    {
        using var response = await Http.SendAsync(GitHubApi(url));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    static async Task<Version?> PluginVersion(string exe, params string[] args)
    {
        var path = Path.Combine(Paths.PluginDir, exe);
        return File.Exists(path) ? ParseVersion((await Shell.Run(path, args)).Output) : null;
    }

    public static Task<Release> LatestStatic() => LatestStatic(StaticIndex, "zip");

    public static async Task<Release> LatestStatic(string index, string extension) =>
        PickStatic(await Http.GetStringAsync(index), index, extension, DateTime.UtcNow);

    internal static Release PickStatic(string html, string index, string extension, DateTime now)
    {
        var entry = new Regex($@"docker-(?<v>\d+\.\d+\.\d+)\.{Regex.Escape(extension)}</a>\s*(?<d>\d{{4}}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}})?");
        var candidates = entry.Matches(html)
            .Select(m => (Version: Version.Parse(m.Groups["v"].Value), Date: ParseDate(m.Groups["d"].Value)))
            .ToList();
        var eligible = candidates.Where(x => x.Date is null || now - x.Date > MinAge).ToList();
        var best = (eligible.Count > 0 ? eligible : candidates).MaxBy(x => x.Version);
        if (best.Version is null) throw new InvalidOperationException("No Docker static release found");
        return new Release(best.Version, best.Version.ToString(), $"{index}docker-{best.Version}.{extension}");
    }

    public static async Task<Release> LatestGitHub(string repo, string assetPattern)
    {
        var releases = JsonNode.Parse(await GetGitHubApi($"https://api.github.com/repos/{repo}/releases?per_page=20"))!.AsArray();
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

    public static async Task<Release?> LatestApp(string assetPattern = @"^Dockyard\.exe$")
    {
        using var response = await Http.SendAsync(GitHubApi($"https://api.github.com/repos/{Paths.Repo}/releases/latest"));
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var tag = release["tag_name"]!.GetValue<string>();
        var asset = release["assets"]!.AsArray().FirstOrDefault(a => Regex.IsMatch(a!["name"]!.GetValue<string>(), assetPattern));
        return ParseVersion(tag) is { } v && asset is not null
            ? new Release(v, tag, asset["browser_download_url"]!.GetValue<string>())
            : null;
    }

    public static async Task<string> DownloadApp(Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = await DownloadReleaseAsset(Path.GetFileName(new Uri(release.DownloadUrl!).LocalPath), dir, tag: release.Tag);
        if (File.Exists(Paths.DashboardExe)) await DownloadReleaseAsset(Paths.DashboardZipName, dir, tag: release.Tag);
        return exe;
    }

    public static Task<string> DownloadImage(string targetDir) => DownloadReleaseAsset(ImageAsset, targetDir, isPattern: true);

    // From the given release; without one, the release of this build, or the latest for development builds.
    internal static string ReleaseApiUrl(string appVersion, string? tag) =>
        tag is not null ? $"https://api.github.com/repos/{Paths.Repo}/releases/tags/{tag}"
        : appVersion == "0.0.0" ? $"https://api.github.com/repos/{Paths.Repo}/releases/latest"
        : $"https://api.github.com/repos/{Paths.Repo}/releases/tags/v{appVersion}";

    // Without a tag: an asset next to the running wizard wins, for offline installs and for testing a build before its release.
    public static async Task<string> ReleaseAssetOrLocal(string pattern, string targetDir, string? tag)
    {
        var local = tag is not null ? null : Directory.EnumerateFiles(Path.GetDirectoryName(Paths.Self)!)
            .FirstOrDefault(f => Regex.IsMatch(Path.GetFileName(f), pattern));
        if (local is null) return await DownloadReleaseAsset(pattern, targetDir, isPattern: true, tag);
        Directory.CreateDirectory(targetDir);
        var copy = Path.Combine(targetDir, Path.GetFileName(local));
        File.Copy(local, copy);
        return copy;
    }

    public static async Task<string> DownloadReleaseAsset(string name, string targetDir, bool isPattern = false, string? tag = null)
    {
        var release = JsonNode.Parse(await GetGitHubApi(ReleaseApiUrl(Paths.AppVersion, tag)))!;
        var assets = release["assets"]!.AsArray();
        var asset = assets.FirstOrDefault(a =>
                isPattern ? Regex.IsMatch(a!["name"]!.GetValue<string>(), name) : a!["name"]!.GetValue<string>() == name)
            ?? throw new InvalidOperationException($"Release {release["tag_name"]} has no {name}");
        var sums = assets.FirstOrDefault(a => a!["name"]!.GetValue<string>() == "SHA256SUMS")
            ?? throw new InvalidOperationException($"Release {release["tag_name"]} has no SHA256SUMS");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, asset["name"]!.GetValue<string>());
        await DownloadFile(asset["browser_download_url"]!.GetValue<string>(), target);
        try
        {
            Checksums.Verify(await Http.GetStringAsync(sums["browser_download_url"]!.GetValue<string>()), target);
        }
        catch
        {
            File.Delete(target);
            throw;
        }
        return target;
    }

    public static async Task DownloadFile(string url, string target)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync();
        await WriteReplacing(content, target);
    }

    // Renamed into place: writing to the target directly would follow a symlink and overwrite what it points at,
    // such as the plugin inside another Docker app's bundle.
    internal static async Task WriteReplacing(Stream content, string target)
    {
        var partial = target + ".download";
        try
        {
            await using (var file = File.Create(partial)) await content.CopyToAsync(file);
            File.Move(partial, target, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    public static Version? ParseVersion(string? text)
    {
        var m = text is null ? null : VersionNumber().Match(text);
        return m is { Success: true } ? Version.Parse(m.Value) : null;
    }

    static DateTime? ParseDate(string text) =>
        DateTime.TryParseExact(text, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();
}
