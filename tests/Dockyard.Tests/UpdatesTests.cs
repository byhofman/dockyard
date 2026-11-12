using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

// Classes that swap Updates.Http, which is static, cannot run side by side.
[CollectionDefinition(nameof(SharedHttp))]
public sealed class SharedHttp;

[Collection(nameof(SharedHttp))]
public sealed class UpdatesTests : IDisposable
{
    const string Base = "https://github.com/byhofman/dockyard/releases";
    readonly HttpClient original = Updates.Http;
    readonly FakeGitHub github = new();
    readonly string dir = Directory.CreateTempSubdirectory().FullName;

    public UpdatesTests() => Updates.Http = new HttpClient(github);

    public void Dispose()
    {
        Updates.Http = original;
        Directory.Delete(dir, recursive: true);
    }

    sealed class FakeGitHub : HttpMessageHandler
    {
        public Dictionary<string, HttpResponseMessage> Routes { get; } = [];
        public List<string> Requested { get; } = [];
        public HttpStatusCode? Refuse { get; set; }

        public void Serve(string url, string body) => Routes[url] = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };

        // HttpClient reports where redirects ended up through the response's request.
        public void Redirect(string url, string target) =>
            Routes[url] = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Head, target) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            if (Refuse is { } status) return Task.FromResult(new HttpResponseMessage(status));
            return Task.FromResult(Routes.TryGetValue(url, out var response) ? response : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static string Sum(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    static string Atom(params (string Tag, DateTime Updated)[] entries) =>
        $"""<?xml version="1.0"?><feed xmlns="http://www.w3.org/2005/Atom"><id>tag:github.com,2008:https://github.com/x/releases</id>{string.Concat(entries.Select(e =>
            $"<entry><id>tag:github.com,2008:Repository/1/{e.Tag}</id><updated>{e.Updated:yyyy-MM-ddTHH:mm:ssZ}</updated><title>{e.Tag}</title></entry>"))}</feed>""";

    void ServeRelease(string tag, string asset, string content, string? sums = null)
    {
        github.Serve($"{Base}/download/{tag}/SHA256SUMS", sums ?? $"{Sum(content)}  {asset}\n");
        github.Serve($"{Base}/download/{tag}/{asset}", content);
    }

    [Fact]
    public async Task The_latest_release_comes_from_the_redirect_and_its_checksums_without_the_api()
    {
        github.Redirect($"{Base}/latest", $"{Base}/tag/v1.2.3");
        ServeRelease("v1.2.3", "Dockyard-linux-x64", "app");

        var release = await Updates.LatestApp(LinuxLayout.AppAsset);

        Assert.Equal(new Version(1, 2, 3), release!.Version);
        Assert.Equal("v1.2.3", release.Tag);
        Assert.Equal($"{Base}/download/v1.2.3/Dockyard-linux-x64", release.DownloadUrl);
        Assert.DoesNotContain(github.Requested, url => url.Contains("api.github.com"));
    }

    [Fact]
    public async Task A_repository_without_releases_has_no_latest()
    {
        github.Redirect($"{Base}/latest", Base);

        Assert.Null(await Updates.LatestApp(LinuxLayout.AppAsset));
    }

    [Fact]
    public async Task A_download_finds_the_asset_by_pattern_and_verifies_it()
    {
        ServeRelease("v1.2.3", "dockyard-linux-1.2.3-x86_64.tar.gz", "image");

        var file = await Updates.DownloadReleaseAsset(Updates.ImageAsset, dir, isPattern: true, tag: "v1.2.3");

        Assert.Equal("image", File.ReadAllText(file));
        Assert.DoesNotContain(github.Requested, url => url.Contains("api.github.com"));
    }

    [Fact]
    public async Task A_download_that_does_not_match_its_checksum_is_removed()
    {
        ServeRelease("v1.2.3", "Dockyard.exe", "tampered", sums: $"{Sum("original")}  Dockyard.exe\n");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Updates.DownloadReleaseAsset("Dockyard.exe", dir, tag: "v1.2.3"));

        Assert.Contains("Checksum mismatch", e.Message);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task An_asset_the_checksums_do_not_list_is_named_in_the_error()
    {
        ServeRelease("v1.2.3", "Dockyard.exe", "app");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Updates.DownloadReleaseAsset("other.zip", dir, tag: "v1.2.3"));

        Assert.Equal("Release v1.2.3 has no other.zip", e.Message);
    }

    [Fact]
    public async Task Tools_come_from_the_newest_stable_release_past_the_waiting_period_without_the_api()
    {
        const string releases = "https://github.com/docker/buildx/releases";
        var now = DateTime.UtcNow;
        github.Serve($"{releases}.atom", Atom(("v0.3.0-rc1", now.AddDays(-30)), ("v0.2.0", now.AddDays(-1)), ("v0.1.1", now.AddDays(-20)), ("v0.1.0", now.AddDays(-40))));
        github.Serve($"{releases}/download/v0.1.1/buildx-v0.1.1.linux-amd64", "");

        var release = await Updates.LatestGitHub("docker/buildx", Updates.LinuxBuildxAsset);

        Assert.Equal("v0.1.1", release.Tag);
        Assert.Equal($"{releases}/download/v0.1.1/buildx-v0.1.1.linux-amd64", release.DownloadUrl);
        Assert.DoesNotContain(github.Requested, url => url.Contains("api.github.com"));
    }

    [Fact]
    public async Task A_tool_release_without_the_asset_is_passed_over()
    {
        const string releases = "https://github.com/docker/buildx/releases";
        var now = DateTime.UtcNow;
        github.Serve($"{releases}.atom", Atom(("v0.2.0", now.AddDays(-30)), ("v0.1.0", now.AddDays(-40))));
        github.Serve($"{releases}/download/v0.1.0/buildx-v0.1.0.linux-amd64", "");

        Assert.Equal("v0.1.0", (await Updates.LatestGitHub("docker/buildx", Updates.LinuxBuildxAsset)).Tag);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_refused_request_says_so_in_words_and_counts_as_rate_limited(HttpStatusCode status)
    {
        github.Refuse = status;

        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Updates.LatestApp(LinuxLayout.AppAsset));

        Assert.True(Updates.IsRateLimited(e));
        Assert.Contains("github.com is refusing or limiting requests", e.Message);
        Assert.DoesNotContain("net_http", e.Message);
    }

    [Fact]
    public async Task Any_other_failure_names_what_could_not_be_fetched()
    {
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => Updates.DownloadReleaseAsset("Dockyard.exe", dir, tag: "v9.9.9"));

        Assert.False(Updates.IsRateLimited(e));
        Assert.Equal($"Could not get {Base}/download/v9.9.9/SHA256SUMS: github.com answered 404 (NotFound).", e.Message);
    }
}
