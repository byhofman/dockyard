using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Setup.Tests;

// Classes that swap Updates.Http or Updates.InstalledComponent, which are static, run alone: a facade made by a test running
// next to them would point InstalledComponent at its own host.
[CollectionDefinition(nameof(SharedHttp), DisableParallelization = true)]
public sealed class SharedHttp;

[Collection(nameof(SharedHttp))]
public sealed class UpdatesTests : IDisposable
{
    const string Base = "https://github.com/byhofman/dockyard/releases";
    readonly HttpClient original = Updates.Http;
    readonly FakeGitHub github = new();
    readonly string dir = Directory.CreateTempSubdirectory().FullName;

    public UpdatesTests()
    {
        Updates.Http = new HttpClient(github);
        Updates.InstalledComponent = _ => Task.FromResult<Version?>(null);
    }

    public void Dispose()
    {
        Updates.Http = original;
        Updates.InstalledComponent = _ => Task.FromResult<Version?>(null);
        Updates.LocalAssets = null;
        Directory.Delete(dir, recursive: true);
    }

    sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];
        public List<string> Requested { get; } = [];
        public HttpStatusCode? Refuse { get; set; }

        public void Serve(string url, string body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (Refuse is { } status) return Task.FromResult(new HttpResponseMessage(status));
            return Task.FromResult(Routes.TryGetValue(url, out var response) ? response() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static string Sum(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    void ServeRelease(string tag, string asset, string content, string? sums = null)
    {
        github.Serve($"{Base}/download/{tag}/SHA256SUMS", sums ?? $"{Sum(content)}  {asset}\n");
        github.Serve($"{Base}/download/{tag}/{asset}", content);
    }

    [Fact]
    public async Task A_component_s_latest_release_comes_from_the_index_and_its_checksums_without_the_api()
    {
        github.Serve($"{Base}/download/index/components.json", """{ "cli": { "version": "1.2.3", "tag": "cli/1.2.3" }, "tray": { "version": "1.1.0", "tag": "tray/1.1.0" } }""");
        ServeRelease("cli/1.2.3", "dockyard_1.2.3_amd64.deb", "app");
        ServeRelease("tray/1.1.0", "Dockyard.Tray.zip", "tray");

        var release = await Updates.LatestComponentAsset(Updates.CliComponent, LinuxLayout.AppPackage("apt"));
        var tray = await Updates.LatestComponentAsset(Updates.TrayComponent, Updates.TrayZipPattern);

        Assert.Equal(new Version(1, 2, 3), release!.Version);
        Assert.Equal("cli/1.2.3", release.Tag);
        Assert.Equal($"{Base}/download/cli/1.2.3/dockyard_1.2.3_amd64.deb", release.DownloadUrl);
        Assert.Equal("tray/1.1.0", tray!.Tag);
        Assert.DoesNotContain(github.Requested, url => url.Contains("api.github.com"));
    }

    [Theory]
    [InlineData("Dockyard.Tray.zip", true)]
    [InlineData("dockyard-tray-windows-x64.exe", false)]
    [InlineData("Dockyard.Dashboard.zip", false)]
    [InlineData("dockyard-windows-x64.exe", false)]
    public void The_Windows_tray_is_found_by_its_own_asset_name(string asset, bool matches) =>
        Assert.Equal(matches, System.Text.RegularExpressions.Regex.IsMatch(asset, Updates.TrayZipPattern));

    [Fact]
    public async Task Without_its_component_in_the_index_nothing_is_downloaded()
    {
        github.Serve($"{Base}/download/index/components.json", """{ "tray": { "version": "1.1.0", "tag": "tray/1.1.0" } }""");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Updates.DownloadComponentAsset(Updates.EngineComponent, Updates.ImageAsset, dir));

        Assert.Contains("which engine release is the latest", e.Message);
    }

    [Fact]
    public async Task An_asset_comes_from_the_installed_release_of_the_component_that_owns_it()
    {
        github.Serve($"{Base}/download/index/components.json", """{ "cli": { "version": "3.0.0", "tag": "cli/3.0.0" }, "dashboard": { "version": "3.0.0", "tag": "dashboard/3.0.0" } }""");
        ServeRelease("dashboard/1.1.0", "Dockyard.Dashboard.zip", "installed dashboard");
        Updates.InstalledComponent = c => Task.FromResult<Version?>(c == Updates.DashboardComponent ? new Version(1, 1, 0) : new Version(2, 0, 0));

        var file = await Updates.DownloadComponentAsset(Updates.DashboardComponent, Updates.DashboardZipPattern, dir);

        Assert.Equal("installed dashboard", File.ReadAllText(file));
        Assert.DoesNotContain(github.Requested, url => url.Contains("/cli/"));
    }

    [Fact]
    public async Task A_component_that_is_not_there_comes_from_its_latest_release()
    {
        github.Serve($"{Base}/download/index/components.json", """{ "cli": { "version": "2.0.0", "tag": "cli/2.0.0" }, "engine": { "version": "1.4.0", "tag": "engine/1.4.0" } }""");
        ServeRelease("engine/1.4.0", "dockyard-vm-1.4.0-aarch64.tar.gz", "vm");
        Updates.InstalledComponent = c => Task.FromResult<Version?>(c == Updates.CliComponent ? new Version(2, 0, 0) : null);

        var file = await Updates.DownloadComponentAsset(Updates.EngineComponent, Updates.VmAsset, dir);

        Assert.Equal("vm", File.ReadAllText(file));
    }

    [Fact]
    public async Task A_named_release_wins_over_the_installed_one_and_over_local_files()
    {
        ServeRelease("tray/1.3.0", "Dockyard.Tray.zip", "named");
        File.WriteAllText(Path.Combine(dir, "Dockyard.Tray.zip"), "local");
        var target = Directory.CreateTempSubdirectory().FullName;
        Updates.LocalAssets = dir;
        Updates.InstalledComponent = _ => Task.FromResult<Version?>(new Version(1, 0, 0));
        try
        {
            Assert.Equal("named", File.ReadAllText(await Updates.DownloadComponentAsset(Updates.TrayComponent, Updates.TrayZipPattern, target, tag: "tray/1.3.0")));
            Assert.Equal("local", File.ReadAllText(await Updates.DownloadComponentAsset(Updates.TrayComponent, Updates.TrayZipPattern, Path.Combine(target, "again"))));
        }
        finally { Directory.Delete(target, recursive: true); }
    }

    [Fact]
    public async Task An_update_waits_until_every_component_it_requires_is_new_enough()
    {
        github.Serve($"{Base}/download/index/components.json",
            """{ "dashboard": { "version": "2.0.0", "tag": "dashboard/2.0.0", "requires": { "cli": "2.0.0", "tray": "1.5.0" } } }""");
        ServeRelease("dashboard/2.0.0", "Dockyard.Dashboard.zip", "dashboard");
        Dictionary<string, Version> installed = new() { [Updates.CliComponent] = new(2, 0, 0), [Updates.TrayComponent] = new(1, 4, 0) };
        Updates.InstalledComponent = c => Task.FromResult<Version?>(installed.GetValueOrDefault(c));

        Assert.Null(await Updates.LatestComponentAsset(Updates.DashboardComponent, Updates.DashboardZipPattern));
        installed[Updates.TrayComponent] = new(1, 5, 0);
        Assert.Equal("dashboard/2.0.0", (await Updates.LatestComponentAsset(Updates.DashboardComponent, Updates.DashboardZipPattern))!.Tag);
    }

    [Fact]
    public async Task A_download_finds_the_asset_by_pattern_and_verifies_it()
    {
        ServeRelease("cli/1.2.3", "dockyard-linux-1.2.3-x86_64.tar.gz", "image");

        var file = await Updates.DownloadComponentAsset(Updates.EngineComponent, Updates.ImageAsset, dir, tag: "cli/1.2.3");

        Assert.Equal("image", File.ReadAllText(file));
        Assert.DoesNotContain(github.Requested, url => url.Contains("api.github.com"));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_removed()
    {
        ServeRelease("cli/1.2.3", "dockyard-windows-x64.exe", "tampered", sums: $"{Sum("original")}  dockyard-windows-x64.exe\n");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Updates.DownloadComponentAsset(Updates.CliComponent, Updates.CliAssetPattern, dir, tag: "cli/1.2.3"));

        Assert.Contains("Checksum mismatch", e.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task An_asset_the_checksums_do_not_list_is_named_in_the_error()
    {
        ServeRelease("cli/1.2.3", "dockyard-windows-x64.exe", "app");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Updates.DownloadComponentAsset(Updates.CliComponent, @"^other\.zip$", dir, tag: "cli/1.2.3"));

        Assert.Equal(@"Release cli/1.2.3 has no ^other\.zip$", e.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refused_request_says_so_in_words_and_counts_as_rate_limited(HttpStatusCode status)
    {
        github.Refuse = status;

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Updates.LatestComponentAsset(Updates.CliComponent, Updates.CliAssetPattern));

        Assert.True(Updates.IsRateLimited(e));
        Assert.Contains("github.com is refusing or limiting requests", e.Message);
        Assert.DoesNotContain("net_http", e.Message);
    }

    [Fact]
    public async Task Any_other_failure_names_what_could_not_be_fetched()
    {
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Updates.DownloadComponentAsset(Updates.CliComponent, Updates.CliAssetPattern, dir, tag: "cli/9.9.9"));

        Assert.False(Updates.IsRateLimited(e));
        Assert.Equal($"Could not get {Base}/download/cli/9.9.9/SHA256SUMS: github.com answered 404 (NotFound).", e.Message);
    }

    [Fact]
    public void Nothing_local_is_used_unless_a_folder_was_named()
    {
        File.WriteAllText(Path.Combine(dir, "dockyard-linux-9.9.9-x86_64.tar.gz"), "planted");
        Updates.LocalAssets = null;

        Assert.Null(Updates.LocalAsset(Updates.ImageAsset));
    }

    [Fact]
    public void A_named_folder_gives_its_newest_matching_file()
    {
        File.WriteAllText(Path.Combine(dir, "dockyard-linux-0.4.0-x86_64.tar.gz"), "");
        File.WriteAllText(Path.Combine(dir, "dockyard-linux-0.10.0-x86_64.tar.gz"), "");
        Updates.LocalAssets = dir;
        try
        {
            Assert.Equal(Path.Combine(dir, "dockyard-linux-0.10.0-x86_64.tar.gz"), Updates.LocalAsset(Updates.ImageAsset));
        }
        finally
        {
            Updates.LocalAssets = null;
        }
    }
}
