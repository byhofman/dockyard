using System.Text.RegularExpressions;

namespace Dockyard.Kind;

sealed record NodeImage(string Version, string Reference)
{
    public Version Number => System.Version.Parse(Version.TrimStart('v'));
}

// The node images kind was released with, pinned by digest like every other download. They belong to the pinned kind
// (image/versions.env): a test fails when one moves without the other.
static partial class KindImages
{
    public const string KindVersion = "0.33.0";

    public static readonly IReadOnlyList<NodeImage> All =
    [
        new("v1.37.0", "kindest/node:v1.37.0@sha256:a1ed56cfb0e7b93589bdf97c8cd566405a265939e3620fc4f5de89adff580ae5"),
        new("v1.36.4", "kindest/node:v1.36.4@sha256:099e049362a1526b2db71494e1947aae99bd16290d7c895f2b7ea312e3cbfaed"),
        new("v1.35.8", "kindest/node:v1.35.8@sha256:07b2536e30b803ed61d1677a79df6115f798ce64c80f9e22f6ed45afd09323c0"),
        new("v1.34.11", "kindest/node:v1.34.11@sha256:44e222ee2132dab25ff87301682f89eb82c7880ea3a1bf543bfe9708fd08d67d"),
    ];

    public static NodeImage Default => All[0];

    public static NodeImage? For(string? version) => All.FirstOrDefault(i => i.Version == version);

    // What a cluster running this version can move to; a version that does not parse is offered everything.
    public static IReadOnlyList<NodeImage> NewerThan(string? version) =>
        version is not null && System.Version.TryParse(version.TrimStart('v'), out var current)
            ? All.Where(i => i.Number > current).ToList()
            : All;

    // A version to make a cluster on: the default without one, else one of the pinned images.
    public static NodeImage Choose(string? version) => version is null ? Default
        : For(version.StartsWith('v') ? version : "v" + version)
          ?? throw new InvalidOperationException($"Kubernetes {version} is not one this kind brings: {string.Join(", ", All.Select(i => i.Version))}");

    // Where an upgrade goes: the newest without a version asked for, and never the same or an older one.
    public static NodeImage UpgradeTarget(string? current, string? wanted)
    {
        var newer = NewerThan(current);
        if (newer.Count == 0) throw new InvalidOperationException($"Kubernetes {current} is the newest this kind brings");
        if (wanted is null) return newer[0];
        var image = Choose(wanted);
        return newer.Contains(image) ? image : throw new InvalidOperationException($"Kubernetes {image.Version} is not newer than {current}");
    }

    // The version in a node's image reference, such as kindest/node:v1.37.0@sha256:…
    public static string? VersionOf(string image) => NodeVersion().Match(image) is { Success: true } m ? m.Groups["v"].Value : null;

    [GeneratedRegex(@"^(?:[^@]*/)?node:(?<v>v\d+\.\d+\.\d+)")]
    private static partial Regex NodeVersion();
}
