using System.Net;
using System.Security.Cryptography;
using System.Text;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.Tests;

[Collection(nameof(SharedHttp))]
public sealed class PinnedTests : IDisposable
{
    readonly HttpClient original = Updates.Http;
    readonly Upstream upstream = new();
    readonly string dir = Directory.CreateTempSubdirectory("dockyard-pinned-tests").FullName;

    public PinnedTests() => Updates.Http = new HttpClient(upstream);

    public void Dispose()
    {
        Updates.Http = original;
        Directory.Delete(dir, recursive: true);
    }

    sealed class Upstream : HttpMessageHandler
    {
        public Dictionary<string, string> Files { get; } = [];
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(Files.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    static string Sum(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // Everything any host or the image build asks for, on every platform, whatever system the tests run on.
    public static TheoryData<string> Downloads => new(
        Updates.WindowsDocker.DownloadUrl!, Updates.WindowsCompose.DownloadUrl!, Updates.WindowsBuildx.DownloadUrl!,
        Updates.LinuxDocker.DownloadUrl!, Updates.LinuxCompose.DownloadUrl!, Updates.LinuxBuildx.DownloadUrl!,
        Pinned.Docker("linux", "aarch64", "tgz").DownloadUrl!, Pinned.Compose("docker-compose-linux-aarch64").DownloadUrl!, Pinned.Buildx("linux-arm64").DownloadUrl!,
        MacLayout.Cli.DownloadUrl!, MacLayout.Compose.DownloadUrl!, MacLayout.Buildx.DownloadUrl!, MacLayout.CredentialHelperRelease.DownloadUrl!,
        Pinned.CredentialHelper("wincred", "windows-amd64.exe").DownloadUrl!, Pinned.CredentialHelper("secretservice", "linux-amd64").DownloadUrl!,
        Pinned.Kind("kind-windows-amd64").DownloadUrl!, Pinned.Kind("kind-darwin-arm64").DownloadUrl!, Pinned.Kind("kind-linux-amd64").DownloadUrl!);

    [Theory]
    [MemberData(nameof(Downloads))]
    public void Every_download_has_a_pinned_checksum_over_https(string url)
    {
        Assert.StartsWith("https://", url);
        Assert.Matches("^[0-9a-f]{64}$", Pinned.Sha256Of(url));
    }

    [Fact]
    public void Nothing_is_pinned_that_no_one_downloads()
    {
        Assert.Equal(Downloads.Select(row => row.Data).Order(), Pinned.PinnedUrls.Order());
    }

    [Fact]
    public void A_release_is_the_pinned_version_and_never_a_newer_tag()
    {
        Assert.Equal("https://github.com/docker/compose/releases/download/v5.6.1/docker-compose-linux-x86_64", Updates.LinuxCompose.DownloadUrl);
        Assert.Equal("https://github.com/docker/buildx/releases/download/v0.38.1/buildx-v0.38.1.linux-amd64", Updates.LinuxBuildx.DownloadUrl);
        Assert.Equal("https://download.docker.com/win/static/stable/x86_64/docker-29.9.2.zip", Updates.WindowsDocker.DownloadUrl);
        Assert.Equal(new Version(29, 9, 2), Updates.WindowsDocker.Version);
        Assert.Equal($"https://github.com/kubernetes-sigs/kind/releases/download/{Kind.Tag}/{Kind.Asset}", Kind.Release.DownloadUrl);
        Assert.Equal(Kind.Version, Updates.ParseVersion(Kind.Tag));
    }

    [Fact]
    public void A_file_that_is_not_pinned_is_refused_before_anything_is_downloaded()
    {
        var e = Assert.Throws<InvalidOperationException>(() => Pinned.Sha256Of("https://github.com/docker/compose/releases/download/v9.9.9/docker-compose-linux-x86_64"));

        Assert.Contains("no pinned checksum", e.Message);
        Assert.Empty(upstream.Requested);
    }

    [Fact]
    public void Versions_are_read_as_the_shell_and_the_image_build_read_them()
    {
        var versions = Pinned.ParseVersions("# comment\nDOCKER=29.9.2\r\n\nKIND=0.33.0\n");

        Assert.Equal(new Dictionary<string, string> { ["DOCKER"] = "29.9.2", ["KIND"] = "0.33.0" }, versions);
    }

    [Fact]
    public async Task A_download_that_matches_the_pinned_checksum_is_put_in_place_and_executable()
    {
        upstream.Files["https://example.com/kind"] = "the real kind";
        var target = Path.Combine(dir, "kind");

        await Pinned.Download("https://example.com/kind", Sum("the real kind"), target);

        Assert.Equal("the real kind", File.ReadAllText(target));
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task A_download_that_does_not_match_fails_and_leaves_what_was_there()
    {
        upstream.Files["https://example.com/kind"] = "something else";
        var target = Path.Combine(dir, "kind");
        File.WriteAllText(target, "the kind that was there");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Pinned.Download("https://example.com/kind", Sum("the real kind"), target));

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
