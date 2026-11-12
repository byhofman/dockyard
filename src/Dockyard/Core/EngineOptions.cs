using System.Net;

namespace Dockyard.Core;

// What the user sets for the Linux engine on macOS and in WSL. Text, one entry per line, is how the window edits it.
static class EngineOptions
{
    public static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static string? MirrorProblem(string line) =>
        Uri.TryCreate(line, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.AbsolutePath is "" or "/" && uri.Query.Length == 0
            ? null
            : $"Registry mirror '{line}': use a URL with only a host and port, such as https://mirror.example.com";

    public static string? InsecureRegistryProblem(string line)
    {
        if (line.Contains("://")) return $"Insecure registry '{line}': leave out http://, as in registry.example.com:5000";
        var host = line.Split(':')[0];
        return Uri.CheckHostName(line.Contains('/') ? host.Split('/')[0] : host) == UriHostNameType.Unknown
            ? $"Insecure registry '{line}': use host:port or an address range such as 10.0.0.0/8"
            : null;
    }

    // "10.20.0.0/16 24": a range and the size of each network taken from it.
    public static (AddressPool? Pool, string? Problem) ParsePool(string line)
    {
        var problem = $"Address pool '{line}': use a range and a network size, as in 10.20.0.0/16 24";
        var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var size)) return (null, problem);
        if (!IPNetwork.TryParse(parts[0], out var network) || network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return (null, problem);
        if (size < network.PrefixLength || size > 30) return (null, $"Address pool '{line}': the network size has to be between {network.PrefixLength} and 30");
        return (new AddressPool { Base = parts[0], Size = size }, null);
    }

    public static IReadOnlyList<string> Problems(string? mirrors, string? insecure, string? pools) =>
    [
        .. Lines(mirrors).Select(MirrorProblem).OfType<string>(),
        .. Lines(insecure).Select(InsecureRegistryProblem).OfType<string>(),
        .. Lines(pools).Select(l => ParsePool(l).Problem).OfType<string>(),
    ];
}
