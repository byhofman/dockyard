using System.Reflection;
using Dockyard.Hosts;

namespace Dockyard.Core;

// Every binary Dockyard fetches from someone else is pinned, with its SHA-256, in image/versions.env and
// image/versions.sha256: the image build reads the same two files. Docker's static builds publish no checksums at all,
// and the other projects publish theirs next to the binary, so only a hash reviewed into this repository catches a
// replaced download. A Dockyard release is what moves a version.
static class Pinned
{
    static readonly IReadOnlyDictionary<string, string> Versions = ParseVersions(Resource("versions.env"));
    static readonly string Sums = Resource("versions.sha256");

    // os and arch as download.docker.com names them: win/x86_64, linux/x86_64, mac/aarch64.
    public static Release Docker(string os, string arch, string extension) =>
        Make("DOCKER", v => v, v => $"https://download.docker.com/{os}/static/stable/{arch}/docker-{v}.{extension}");

    public static Release Compose(string asset) => GitHub("docker/compose", "COMPOSE", asset);
    public static Release Buildx(string platform) => GitHub("docker/buildx", "BUILDX", $"buildx-{{tag}}.{platform}");
    public static Release CredentialHelper(string helper, string platform) =>
        GitHub("docker/docker-credential-helpers", "CREDENTIAL_HELPERS", $"docker-credential-{helper}-{{tag}}.{platform}");
    public static Release Kind(string asset) => GitHub("kubernetes-sigs/kind", "KIND", asset);

    public static string Sha256Of(string url) =>
        Checksums.Expected(Sums, url) ?? throw new InvalidOperationException($"{url} has no pinned checksum");

    internal static IEnumerable<string> PinnedUrls => Checksums.Names(Sums);

    public static Task Download(Release release, string target) => Download(release.DownloadUrl!, Sha256Of(release.DownloadUrl!), target);

    // Only a file that matches ever reaches the target, executable; on a mismatch whatever was there stays.
    internal static async Task Download(string url, string sha256, string target)
    {
        var incoming = target + ".verify";
        try
        {
            await Updates.DownloadFile(url, incoming);
            Checksums.VerifyHash(incoming, sha256, Path.GetFileName(new Uri(url).LocalPath));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(incoming, (UnixFileMode)0b111_101_101);
            File.Move(incoming, target, overwrite: true);
        }
        finally
        {
            File.Delete(incoming);
        }
    }

    static Release GitHub(string repo, string key, string assetTemplate) =>
        Make(key, v => $"v{v}", v => $"https://github.com/{repo}/releases/download/v{v}/{assetTemplate.Replace("{tag}", $"v{v}")}");

    // A release whose file has no pinned hash fails here, before anything is downloaded.
    static Release Make(string key, Func<string, string> tag, Func<string, string> url)
    {
        var version = Versions[key];
        var download = url(version);
        Sha256Of(download);
        return new Release(Version.Parse(version), tag(version), download);
    }

    internal static IReadOnlyDictionary<string, string> ParseVersions(string env) => env.Split('\n')
        .Select(line => line.Trim().Split('=', 2))
        .Where(parts => parts.Length == 2 && parts[0].Length > 0 && !parts[0].StartsWith('#'))
        .ToDictionary(parts => parts[0], parts => parts[1]);

    static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
