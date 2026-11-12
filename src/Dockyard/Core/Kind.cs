using Dockyard.Hosts;

namespace Dockyard.Core;

// kind is pinned with its checksums, unlike Compose and Buildx, which follow upstream after 14 days: the checksums kind
// publishes sit next to the binary, so only a hash reviewed into this file catches a replaced download. A Dockyard
// release is what moves it to a newer kind.
static class Kind
{
    public const string Id = "Kind";
    public const string Flag = "kind";
    public const string Title = "kind (Kubernetes in Docker)";
    public const string Note = "Local Kubernetes clusters in containers, on the Linux engine. The dashboard's Clusters section uses it.";
    public const string Tag = "v0.33.0";
    public static readonly Version Version = new(0, 33, 0);

    static readonly Dictionary<string, string> Sha256 = new()
    {
        ["kind-windows-amd64"] = "4b22adaa135368c5a465d56bbd8e520cbea87272a06ca00b6078e7b81515c9fc",
        ["kind-darwin-arm64"] = "0c8c7dbe5e23594a198b786c4bc13dacc101fa6196b0cb0b23a1ca44e61f4b4f",
        ["kind-linux-amd64"] = "aee6151561422756b764a4ae28e7f44cda5af5a9eead3cc9985112b1de8d8e0d",
    };

    public static IEnumerable<string> Assets => Sha256.Keys;
    public static string Sha256Of(string asset) => Sha256[asset];

    public static string Asset =>
        OperatingSystem.IsWindows() ? "kind-windows-amd64" : OperatingSystem.IsMacOS() ? "kind-darwin-arm64" : "kind-linux-amd64";

    public static Release Release => new(Version, Tag, $"https://github.com/kubernetes-sigs/kind/releases/download/{Tag}/{Asset}");

    public static ComponentInfo Component => new(Id, Title, Flag: Flag, Note: Note);

    public static Task Download(string target) => Download(Release.DownloadUrl!, Sha256Of(Asset), target);

    // Only a file that matches ever reaches the target; on a mismatch whatever was there stays.
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
}
