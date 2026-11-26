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

    // "The Dockyard version" is the installed dockyard command's: the tray and the dashboard run this library too, with
    // versions of their own. Only the command itself has none installed yet during a first install, and goes by its own.
    // The facade points InstalledCli at its host.
    internal static Func<Task<Version?>> InstalledCli { get; set; } = () => Task.FromResult<Version?>(null);
    internal static readonly bool RunsAsCli = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name == "dockyard-cli";

    internal static Version? DockyardVersion(Version? installedCli, bool runsAsCli, string ownVersion) =>
        installedCli ?? (runsAsCli ? ParseVersion(ownVersion) : null);

    internal static async Task<Version?> DockyardVersion() => DockyardVersion(await InstalledCli(), RunsAsCli, Paths.AppVersion);

    // A dockyard command run from a download that differs from the installed one brings its own package along. The tray and
    // the dashboard never do: their version is not the command's.
    internal static bool BringsCliAlong(Version? installedCli, bool runsAsCli, string ownVersion) =>
        runsAsCli && installedCli != ParseVersion(ownVersion);

    internal static async Task<bool> BringsCliAlong() => BringsCliAlong(await InstalledCli(), RunsAsCli, Paths.AppVersion);

    [SupportedOSPlatform("windows")]
    static async Task<Version?> OwnToolVersion(CliTool tool) =>
        File.Exists(Paths.ToolExe(tool)) ? ParseVersion((await Shell.Run(Paths.ToolExe(tool), tool.VersionArgs)).Output) : null;

    static async Task<Version?> PluginVersion(string exe, params string[] args)
    {
        var path = Path.Combine(Paths.PluginDir, exe);
        return File.Exists(path) ? ParseVersion((await Shell.Run(path, args)).Output) : null;
    }

    public static async Task<string> DownloadTray(Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return await DownloadReleaseAsset(Path.GetFileName(new Uri(release.DownloadUrl!).LocalPath), dir, tag: release.Tag);
    }

    public static Task<string> DownloadImage(string targetDir) => DownloadComponentAsset(EngineComponent, ImageAsset, targetDir);

    // Components are released on their own, tagged <component>/<version>; components.json on the release tagged index
    // names each one's latest. See docs/design/components-and-releases.md.
    public const string DashboardZipPattern = @"^Dockyard\.Dashboard\.zip$";
    public const string CliAssetPattern = @"^dockyard-windows-x64\.exe$";
    public const string TrayComponent = "tray";
    public const string DashboardComponent = "dashboard";
    public const string CliComponent = "cli";
    public const string EngineComponent = "engine";

    public static string ComponentTag(string component, string version) => $"{component}/{version}";

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

    // A component that needs a newer dockyard command waits for the command's own update, which comes first: the command
    // installs everything else. A development build (0.0.0) is taken as meeting every requirement.
    internal static bool RequirementsMet(ComponentEntry entry, Version? cli) =>
        !entry.Requires.TryGetValue(CliComponent, out var needed) || cli is null || cli == new Version(0, 0, 0) || cli >= needed;

    // A component's latest release, or null when the index does not name it.
    internal static async Task<ComponentEntry?> LatestComponent(string component) =>
        (await Index()).GetValueOrDefault(component);

    public static async Task<Release?> LatestComponentAsset(string component, string assetPattern)
    {
        if (await LatestComponent(component) is not { } entry) return null;
        if (!RequirementsMet(entry, await DockyardVersion())) return null;
        if (await GetTextIfFound(AssetUrl(Paths.Repo, entry.Tag, "SHA256SUMS")) is not { } sums) return null;
        return Checksums.Names(sums).FirstOrDefault(n => Regex.IsMatch(n, assetPattern)) is { } asset
            ? new Release(entry.Version, entry.Tag, AssetUrl(Paths.Repo, entry.Tag, asset))
            : null;
    }

    // --assets wins; otherwise the component's own latest release.
    public static async Task<string> DownloadComponentAsset(string component, string assetPattern, string targetDir)
    {
        if (LocalAsset(assetPattern) is { } local)
        {
            Directory.CreateDirectory(targetDir);
            var copy = Path.Combine(targetDir, Path.GetFileName(local));
            File.Copy(local, copy, overwrite: true);
            return copy;
        }
        var entry = await LatestComponent(component)
            ?? throw new InvalidOperationException($"Could not read which {component} release is the latest (components.json on the release tagged index); check the connection to github.com, or pass the files with --assets");
        return await DownloadReleaseAsset(assetPattern, targetDir, isPattern: true, tag: entry.Tag);
    }

    // From the given release; without one, the CLI release of the Dockyard version, so what is added matches what is
    // installed. A development build (0.0.0) or no version at all leaves it to the index's cli entry. The CLI's release also
    // carries the macOS app and the Linux packages and tarball.
    internal static string? ReleaseTag(Version? dockyard, string? tag) =>
        tag ?? (dockyard is null || dockyard == new Version(0, 0, 0) ? null : ComponentTag(CliComponent, dockyard.ToString()));

    // --assets: Dockyard's own files from a folder the user named, for offline installs and for testing a build before its
    // release. Never the installer's own folder by itself: that is usually Downloads, where any web page can leave a file.
    public static string? LocalAssets { get; set; }

    public static string? LocalAsset(string pattern) => LocalAssets is null ? null
        : Directory.EnumerateFiles(LocalAssets).Where(f => Regex.IsMatch(Path.GetFileName(f), pattern))
            .OrderByDescending(f => ParseVersion(Path.GetFileName(f))).FirstOrDefault();

    // Without a tag, a file given with --assets wins.
    public static async Task<string> ReleaseAssetOrLocal(string pattern, string targetDir, string? tag)
    {
        var local = tag is not null ? null : LocalAsset(pattern);
        if (local is null) return await DownloadReleaseAsset(pattern, targetDir, isPattern: true, tag);
        Directory.CreateDirectory(targetDir);
        var copy = Path.Combine(targetDir, Path.GetFileName(local));
        File.Copy(local, copy);
        return copy;
    }

    public static async Task<string> DownloadReleaseAsset(string name, string targetDir, bool isPattern = false, string? tag = null)
    {
        var release = ReleaseTag(await DockyardVersion(), tag) ?? (await LatestComponent(CliComponent))?.Tag
            ?? throw new InvalidOperationException("No Dockyard release found: components.json on the release tagged index names no cli release");
        var sums = await GetText(AssetUrl(Paths.Repo, release, "SHA256SUMS"));
        var asset = Checksums.Names(sums).FirstOrDefault(n => isPattern ? Regex.IsMatch(n, name) : n == name)
            ?? throw new InvalidOperationException($"Release {release} has no {name}");
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
