using System.Text.Json.Serialization;

namespace Dockyard.Core;

// Where docker, kind, kubectl and helm come from: the system's package manager (winget, Homebrew, apt, dnf), or Dockyard's
// own pinned downloads.
[JsonConverter(typeof(JsonStringEnumConverter<ToolSource>))]
public enum ToolSource
{
    [JsonStringEnumMemberName("package-manager")] PackageManager,
    [JsonStringEnumMemberName("dockyard")] Dockyard,
}

static class ToolSourceArgument
{
    public static string Argument(this ToolSource source) => source == ToolSource.Dockyard ? "dockyard" : "package-manager";

    public static ToolSource? Parse(string? argument) => argument switch
    {
        "dockyard" => ToolSource.Dockyard,
        "package-manager" => ToolSource.PackageManager,
        _ => null,
    };
}
