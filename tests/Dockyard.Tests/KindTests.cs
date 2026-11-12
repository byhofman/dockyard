using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

[Collection(nameof(SharedHttp))]
public sealed class KindTests : IDisposable
{
    readonly HttpClient original = Updates.Http;
    readonly Upstream upstream = new();
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-kind-tests").FullName;

    public KindTests() => Updates.Http = new HttpClient(upstream);

    public void Dispose()
    {
        Updates.Http = original;
        Directory.Delete(dir, recursive: true);
    }

    sealed class Upstream : HttpMessageHandler
    {
        public Dictionary<string, string> Files { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    static string Sum(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Fact]
    public void Every_platform_Dockyard_runs_on_has_a_pinned_binary_with_its_checksum()
    {
        Assert.Equal(["kind-darwin-arm64", "kind-linux-amd64", "kind-windows-amd64"], Kind.Assets.Order());
        foreach (var asset in Kind.Assets) Assert.Matches("^[0-9a-f]{64}$", Kind.Sha256Of(asset));
        Assert.Contains(Kind.Asset, Kind.Assets);
    }

    [Fact]
    public void The_release_is_the_pinned_one_and_never_a_newer_tag()
    {
        Assert.Equal(Kind.Version, Kind.Release.Version);
        Assert.Equal($"https://github.com/kubernetes-sigs/kind/releases/download/{Kind.Tag}/{Kind.Asset}", Kind.Release.DownloadUrl);
        Assert.Equal(Kind.Version, Updates.ParseVersion(Kind.Tag));
    }

    [Fact]
    public async Task A_download_that_matches_the_pinned_checksum_is_put_in_place_and_executable()
    {
        upstream.Files["https://example.com/kind"] = "the real kind";
        var target = Path.Combine(dir, "kind");

        await Kind.Download("https://example.com/kind", Sum("the real kind"), target);

        Assert.Equal("the real kind", File.ReadAllText(target));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task A_download_that_does_not_match_fails_and_leaves_what_was_there()
    {
        upstream.Files["https://example.com/kind"] = "something else";
        var target = Path.Combine(dir, "kind");
        File.WriteAllText(target, "the kind that was there");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Kind.Download("https://example.com/kind", Sum("the real kind"), target));

        Assert.Contains("Checksum mismatch", e.Message);
        Assert.Equal("the kind that was there", File.ReadAllText(target));
        Assert.Equal(["kind"], Directory.GetFiles(dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Kind_is_a_component_with_a_flag_on_every_platform()
    {
        Assert.Contains("kind", ComponentRules.KnownFlags);
        Assert.Contains("kind", SetupOptions.Usage);
        Assert.Equal(["kind"], SetupOptions.Parse(["--silent", "--with", "kind"]).With);
#pragma warning disable CA1416 // Only the catalogs, which are plain data on any system.
        var mac = MacHost.Catalog.Single(c => c.Id == Kind.Id);
        Assert.Equal("kind", mac.Flag);
        Assert.True(mac.IsOptional);
        Assert.Contains(WindowsHost.Catalog, c => c.Id == Kind.Id && c.Flag == "kind");
        Assert.Contains(LinuxHost.Catalog, c => c.Id == Kind.Id && c.Flag == "kind");
#pragma warning restore CA1416
    }

    [Fact]
    public void Removing_the_Windows_engine_keeps_kind_with_the_CLI()
    {
        Assert.True(WindowsEngineFiles.Keep("docker.exe"));
        Assert.True(WindowsEngineFiles.Keep("kind.exe"));
        Assert.True(WindowsEngineFiles.Keep("docker-credential-wincred.exe"));
        Assert.False(WindowsEngineFiles.Keep("dockerd.exe"));
    }
}
