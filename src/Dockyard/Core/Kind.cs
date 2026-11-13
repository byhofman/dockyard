using Dockyard.Hosts;

namespace Dockyard.Core;

static class Kind
{
    public const string Id = "Kind";
    public const string Flag = "kind";
    public const string Title = "kind (Kubernetes in Docker)";
    public const string Note = "Local Kubernetes clusters in containers, on the Linux engine. The dashboard's Clusters section uses it.";

    public static string Asset =>
        OperatingSystem.IsWindows() ? "kind-windows-amd64" : OperatingSystem.IsMacOS() ? "kind-darwin-arm64" : "kind-linux-amd64";

    public static Release Release => Pinned.Kind(Asset);
    public static Version Version => Release.Version;
    public static string Tag => Release.Tag;

    public static ComponentInfo Component => new(Id, Title, Flag: Flag, Note: Note);

    public static Task Download(string target) => Pinned.Download(Release, target);
}
