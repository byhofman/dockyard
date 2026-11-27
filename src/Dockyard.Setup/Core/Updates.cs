using System.Net;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Dockyard.Hosts;

namespace Dockyard.Core;

static partial class Updates
{
    public const string TrayZipPattern = @"^Dockyard\.Tray\.zip$";
    public const string ImageAsset = @"^dockyard-linux-[\d.]+-x86_64\.tar\.gz$";

    internal static HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dockyard");
        return http;
    }

    public static string Title(Component c) => c switch
    {
        Component.Engine => "Docker Engine (Windows)",
        Component.DockerCli => "Docker CLI",
        Component.Dashboard => "Dashboard",
        Component.Compose => "Docker Compose",
        Component.Buildx => "Docker Buildx",
        Component.LinuxEngine => "Docker Engine (Linux, WSL)",
        Component.DockyardCli => "Dockyard",
        Component.Tray => "Dockyard tray",
        Component.Kind => Tools.Kind.Title,
        Component.Kubectl => Tools.Kubectl.Title,
        Component.Helm => Tools.Helm.Title,
        _ => c.ToString(),
    };

    [SupportedOSPlatform("windows")]
    public static async Task<Version?> Installed(Component c) => c switch
    {
        Component.DockyardCli => File.Exists(Paths.DockyardCliExe) ? ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(Paths.DockyardCliExe).ProductVersion) : null,
        Component.Engine => File.Exists(Paths.DockerdExe) ? ParseVersion(await Shell.Check(Paths.DockerdExe, "--version")) : null,
        Component.DockerCli => File.Exists(Paths.DockerExe) ? ParseVersion((await Shell.Run(Paths.DockerExe, "--version")).Output) : null,
        Component.Dashboard => File.Exists(Paths.DashboardExe) ? ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(Paths.DashboardExe).ProductVersion) : null,
        Component.Compose => await PluginVersion("docker-compose.exe", "version", "--short"),
        Component.Buildx => await PluginVersion("docker-buildx.exe", "version"),
        Component.Kind or Component.Kubectl or Component.Helm => await OwnToolVersion(Tools.Find(c.ToString())!),
        Component.LinuxEngine => Settings.Load().RemovedComponents.Contains(nameof(Component.LinuxEngine)) ? null : await new WslDistro(ProcessRunner.Instance).DockerVersion(),
        // Setup runs from a download and can be newer than what is installed, so not this process's own version.
        Component.Tray => File.Exists(Paths.AppExe) ? ParseVersion(System.Diagnostics.FileVersionInfo.GetVersionInfo(Paths.AppExe).ProductVersion) : null,
        _ => null,
    };

    public static async Task<Release?> Latest(Component c) => c switch
    {
        Component.DockyardCli => await LatestComponentAsset(CliComponent, CliAssetPattern),
        Component.Engine or Component.DockerCli => WindowsDocker,
        Component.Dashboard => await LatestComponentAsset(DashboardComponent, DashboardZipPattern),
        Component.Compose => WindowsCompose,
        Component.Buildx => WindowsBuildx,
        Component.LinuxEngine => LinuxDocker,
        Component.Tray => await LatestComponentAsset(TrayComponent, TrayZipPattern),
        Component.Kind or Component.Kubectl or Component.Helm => Tools.Pinned(Tools.Find(c.ToString())!),
        _ => null,
    };

    public static Release WindowsDocker => Pinned.Docker("win", "x86_64", "zip");
    public static Release WindowsCompose => Pinned.Compose("docker-compose-windows-x86_64.exe");
    public static Release WindowsBuildx => Pinned.Buildx("windows-amd64.exe");
    public static Release LinuxDocker => Pinned.Docker("linux", "x86_64", "tgz");
    public static Release LinuxCompose => Pinned.Compose("docker-compose-linux-x86_64");
    public static Release LinuxBuildx => Pinned.Buildx("linux-amd64");

    public static bool IsRateLimited(HttpRequestException e) => IsRateLimit(e.StatusCode);

    static bool IsRateLimit(HttpStatusCode? status) => status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests;

    static void EnsureSuccess(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;
        var host = new Uri(url).Host;
        var code = (int)response.StatusCode;
        var message = IsRateLimit(response.StatusCode)
            ? $"{host} is refusing or limiting requests from this network right now ({code}). Try again later."
            : $"Could not get {url}: {host} answered {code} ({response.StatusCode}).";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    static async Task<string> GetText(string url)
    {
        using var response = await Http.GetAsync(url);
        EnsureSuccess(response, url);
        return await response.Content.ReadAsStringAsync();
    }

    static async Task<string?> GetTextIfFound(string url)
    {
        try
        {
            return await GetText(url);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // Releases are read from github.com and not api.github.com: the API allows 60 anonymous calls an hour per address,
    // which everyone behind one office network or VPN shares.
    static string AssetUrl(string repo, string tag, string asset) => $"https://github.com/{repo}/releases/download/{tag}/{asset}";

    // The installed version of a component (cli, dashboard, tray, engine), or null when it is not there or not known. The
    // facade points it at its host.
    internal static Func<string, Task<Version?>> InstalledComponent { get; set; } = _ => Task.FromResult<Version?>(null);

    // The running app's own component is what its setup puts in place, so it goes by its own version; any other by what is
    // installed. A development build (0.0.0) has no release of its own.
    internal static Version? VersionOf(string component, Version? installed, RunningApp app, string ownVersion) =>
        ThisApp.OwnComponent(app) == component && ParseVersion(ownVersion) is { } own && own != Development ? own : installed;

    static async Task<Version?> VersionOf(string component) =>
        VersionOf(component, await InstalledComponent(component), ThisApp.Current, Paths.AppVersion);

    static readonly Version Development = new(0, 0, 0);

    [SupportedOSPlatform("windows")]
    static async Task<Version?> OwnToolVersion(CliTool tool) =>
        File.Exists(Paths.ToolExe(tool)) ? ParseVersion((await Shell.Run(Paths.ToolExe(tool), tool.VersionArgs)).Output) : null;

    static async Task<Version?> PluginVersion(string exe, params string[] args)
    {
        var path = Path.Combine(Paths.PluginDir, exe);
        return File.Exists(path) ? ParseVersion((await Shell.Run(path, args)).Output) : null;
    }

    public static Task<string> DownloadTray(Release release) =>
        DownloadComponentAsset(TrayComponent, TrayZipPattern, Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}"), release.Tag);

    public static Task<string> DownloadImage(string targetDir) => DownloadComponentAsset(EngineComponent, ImageAsset, targetDir);

    // Components are released on their own, tagged <component>/<version>; components.json on the release tagged index
    // names each one's latest. See docs/design/components-and-releases.md.
    public const string DashboardZipPattern = @"^Dockyard\.Dashboard\.zip$";
    public const string CliAssetPattern = @"^dockyard-windows-x64\.exe$";
    public const string TrayComponent = "tray";
    public const string DashboardComponent = "dashboard";
    public const string CliComponent = "cli";
    public const string EngineComponent = "engine";
    public const string VmAsset = @"^dockyard-vm-[\d.]+-aarch64\.tar\.gz$";

    public static string ComponentTag(string component, string version) => $"{component}/v{version}";

    internal sealed record ComponentEntry(string Tag, Version Version, IReadOnlyDictionary<string, Version> Requires);

    internal static Dictionary<string, ComponentEntry> ParseIndex(string json)
    {
        var entries = new Dictionary<string, ComponentEntry>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var tag = p.Value.TryGetProperty("tag", out var t) ? t.GetString() : null;
                var version = ParseVersion(p.Value.TryGetProperty("version", out var v) ? v.GetString() : null);
                if (tag is null || version is null) continue;
                var requires = new Dictionary<string, Version>();
                if (p.Value.TryGetProperty("requires", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Object)
                    foreach (var q in r.EnumerateObject())
                        if (ParseVersion(q.Value.GetString()) is { } min) requires[q.Name] = min;
                entries[p.Name] = new ComponentEntry(tag, version, requires);
            }
        }
        catch (System.Text.Json.JsonException) { }
        return entries;
    }

    static async Task<Dictionary<string, ComponentEntry>> Index() =>
        await GetTextIfFound(AssetUrl(Paths.Repo, "index", "components.json")) is { } json ? ParseIndex(json) : [];

    // An update waits until every component it names is at least that version: that component's own update comes first.
    // One that is not there, or a development build (0.0.0), stands in the way of nothing.
    internal static bool RequirementsMet(ComponentEntry entry, Func<string, Version?> versionOf) =>
        entry.Requires.All(r => versionOf(r.Key) is not { } version || version == Development || version >= r.Value);

    static async Task<bool> RequirementsMet(ComponentEntry entry)
    {
        Dictionary<string, Version?> versions = [];
        foreach (var name in entry.Requires.Keys) versions[name] = await VersionOf(name);
        return RequirementsMet(entry, versions.GetValueOrDefault);
    }

    // A component's latest release, or null when the index does not name it.
    internal static async Task<ComponentEntry?> LatestComponent(string component) =>
        (await Index()).GetValueOrDefault(component);

    public static async Task<Release?> LatestComponentAsset(string component, string assetPattern)
    {
        if (await LatestComponent(component) is not { } entry) return null;
        if (!await RequirementsMet(entry)) return null;
        if (await GetTextIfFound(AssetUrl(Paths.Repo, entry.Tag, "SHA256SUMS")) is not { } sums) return null;
        return Checksums.Names(sums).FirstOrDefault(n => Regex.IsMatch(n, assetPattern)) is { } asset
            ? new Release(entry.Version, entry.Tag, AssetUrl(Paths.Repo, entry.Tag, asset))
            : null;
    }

    // The release of the component that owns the asset: the one named; otherwise that component's version here, so what is
    // added matches what is installed; otherwise its latest. Without a release named, a file given with --assets wins.
    public static async Task<string> DownloadComponentAsset(string component, string assetPattern, string targetDir, string? tag = null)
    {
        if (tag is null && LocalAsset(assetPattern) is { } local)
        {
            Directory.CreateDirectory(targetDir);
            var copy = Path.Combine(targetDir, Path.GetFileName(local));
            File.Copy(local, copy, overwrite: true);
            return copy;
        }
        var release = ReleaseTag(component, await VersionOf(component), tag) ?? (await LatestRequirementsMet(component)).Tag;
        return await DownloadReleaseAsset(release, assetPattern, targetDir);
    }

    // The index holds one release per component, so the newest one that fits is the latest, or none.
    static async Task<ComponentEntry> LatestRequirementsMet(string component)
    {
        var entry = await LatestComponent(component)
            ?? throw new InvalidOperationException($"Could not read which {component} release is the latest (components.json on the release tagged index); check the connection to github.com, or pass the files with --assets");
        if (await RequirementsMet(entry)) return entry;
        var needed = string.Join(", ", entry.Requires.Select(r => $"{r.Key} {r.Value}"));
        throw new InvalidOperationException($"The latest {component} release needs {needed}; update that first, then try again");
    }

    internal static string? ReleaseTag(string component, Version? version, string? tag) =>
        tag ?? (version is null || version == Development ? null : ComponentTag(component, version.ToString()));

    // --assets: Dockyard's own files from a folder the user named, for offline installs and for testing a build before its
    // release. Never the installer's own folder by itself: that is usually Downloads, where any web page can leave a file.
    public static string? LocalAssets { get; set; }

    public static string? LocalAsset(string pattern) => LocalAssets is null ? null
        : Directory.EnumerateFiles(LocalAssets).Where(f => Regex.IsMatch(Path.GetFileName(f), pattern))
            .OrderByDescending(f => ParseVersion(Path.GetFileName(f))).FirstOrDefault();

    static async Task<string> DownloadReleaseAsset(string release, string assetPattern, string targetDir)
    {
        var sums = await GetText(AssetUrl(Paths.Repo, release, "SHA256SUMS"));
        var asset = Checksums.Names(sums).FirstOrDefault(n => Regex.IsMatch(n, assetPattern))
            ?? throw new InvalidOperationException($"Release {release} has no {assetPattern}");
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, asset);
        await DownloadFile(AssetUrl(Paths.Repo, release, asset), target);
        try
        {
            Checksums.Verify(sums, target);
        }
        catch
        {
            File.Delete(target);
            throw;
        }
        return target;
    }

    // A dropped connection on a large file should not fail a whole install; what is not found stays an error at once.
    public static async Task DownloadFile(string url, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                EnsureSuccess(response, url);
                await using var content = await response.Content.ReadAsStreamAsync();
                await WriteReplacing(content, target);
                return;
            }
            catch (Exception e) when (attempt < 3 && e is IOException or HttpRequestException { StatusCode: null })
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
            }
        }
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

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionNumber();
}
