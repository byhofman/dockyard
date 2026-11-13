using System.Text;

namespace Dockyard.Agent;

static class DomainNames
{
    public const string Zone = "dockyard.test";
    public const string PortLabel = "dockyard.port";
    // People moving from OrbStack have this label in their Compose files already.
    public const string OrbStackPortLabel = "dev.orbstack.http-port";

    static string Clean(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return builder.ToString().Trim('-');
    }

    public static bool InZone(string name) => name == Zone || name.EndsWith("." + Zone, StringComparison.Ordinal);

    // A flat name on purpose: a certificate's wildcard covers exactly one label, so web.shop.dockyard.test would need a
    // new certificate per project and the app holding the CA to answer for each.
    public static IEnumerable<string> For(string containerName, string? project, string? service)
    {
        var own = Clean(containerName);
        if (own.Length > 0) yield return $"{own}.{Zone}";
        if (project is { Length: > 0 } && service is { Length: > 0 }) yield return $"{Clean(project)}-{Clean(service)}.{Zone}";
    }

    // Chosen by the user over labelled over detected. Among what is listening or exposed, the usual web ports first,
    // since an app often also opens a debug or metrics port with a lower number.
    static readonly int[] WebPorts = [80, 8080, 3000, 8000, 5000, 4000, 5173, 4200, 8888, 9000];

    public static int? PickPort(int? chosen, int? label, IEnumerable<int> listening, IEnumerable<int> exposed)
    {
        if (chosen is { } c) return c;
        if (label is { } l) return l;
        var heard = listening.ToHashSet();
        var declared = exposed.ToHashSet();
        foreach (var port in WebPorts) if (heard.Contains(port)) return port;
        foreach (var port in WebPorts) if (declared.Contains(port)) return port;
        if (heard.Count > 0) return heard.Min();
        return declared.Count > 0 ? declared.Min() : null;
    }

    public static int? LabelPort(IReadOnlyDictionary<string, string>? labels) =>
        labels is not null && (labels.TryGetValue(PortLabel, out var a) || labels.TryGetValue(OrbStackPortLabel, out a)) && int.TryParse(a, out var port) && port is > 0 and <= 65535 ? port : null;
}
