using YamlDotNet.RepresentationModel;

namespace Dockyard.Dashboard.Features.Clusters;

// A kind cluster's three entries in a kubeconfig, all named kind-<cluster> as kind names them. Everything else in the
// file is left as it was read.
static class KindKubeconfig
{
    static readonly string[] Lists = ["clusters", "users", "contexts"];
    const string CurrentContext = "current-context";
    // Where the context remembers what was current before it, so deleting the cluster can go back to that.
    const string Extension = "dockyard";
    const string PreviousContext = "previous-context";

    public static string Key(string cluster) => "kind-" + cluster;

    // Adds or replaces the cluster's entries from kind's own kubeconfig for it, and makes its context the current one.
    public static void Merge(YamlMappingNode config, YamlMappingNode kind, string cluster)
    {
        var key = Key(cluster);
        var previous = Scalar(config, CurrentContext) is { } current && current != key ? current : PreviousOf(Find(config, "contexts", key));
        foreach (var list in Lists)
        {
            var entry = Find(kind, list, key) ?? throw new InvalidDataException($"kind's kubeconfig for {cluster} has no {list} entry named {key}");
            var entries = List(config, list, create: true)!;
            var at = IndexOf(entries, key);
            if (at >= 0) entries.Children[at] = entry;
            else entries.Add(entry);
        }
        if (previous is not null) Remember(Find(config, "contexts", key)!, previous);
        config.Children[new YamlScalarNode(CurrentContext)] = new YamlScalarNode(key);
    }

    // Takes the cluster's entries out. When its context was the current one, the context that was current before it
    // comes back if it still exists anywhere, otherwise none is current, as kind leaves it.
    public static bool Remove(YamlMappingNode config, string cluster, Func<string, bool> contextExists)
    {
        var key = Key(cluster);
        var previous = PreviousOf(Find(config, "contexts", key));
        var changed = false;
        foreach (var list in Lists)
        {
            if (List(config, list, create: false) is not { } entries) continue;
            int at;
            while ((at = IndexOf(entries, key)) >= 0)
            {
                entries.Children.RemoveAt(at);
                changed = true;
            }
        }
        if (Scalar(config, CurrentContext) == key)
        {
            if (previous is not null && contextExists(previous)) config.Children[new YamlScalarNode(CurrentContext)] = new YamlScalarNode(previous);
            else config.Children.Remove(new YamlScalarNode(CurrentContext));
            changed = true;
        }
        return changed;
    }

    public static string? Current(YamlMappingNode config) => Scalar(config, CurrentContext);

    public static IEnumerable<string> ContextNames(YamlMappingNode config) =>
        List(config, "contexts", create: false)?.Children.OfType<YamlMappingNode>().Select(e => Scalar(e, "name")).OfType<string>() ?? [];

    static YamlSequenceNode? List(YamlMappingNode config, string name, bool create)
    {
        if (!config.Children.TryGetValue(new YamlScalarNode(name), out var node) || IsNull(node))
        {
            if (!create) return null;
            var made = new YamlSequenceNode();
            config.Children[new YamlScalarNode(name)] = made;
            return made;
        }
        return node as YamlSequenceNode ?? throw new InvalidDataException($"'{name}' in the kubeconfig is not a list");
    }

    static YamlMappingNode? Find(YamlMappingNode config, string list, string key) =>
        List(config, list, create: false) is { } entries && IndexOf(entries, key) is >= 0 and var at ? (YamlMappingNode)entries.Children[at] : null;

    static int IndexOf(YamlSequenceNode entries, string key) =>
        entries.Children.ToList().FindIndex(e => e is YamlMappingNode m && Scalar(m, "name") == key);

    static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode { Value: { Length: > 0 } s } && !IsNull(value) ? s : null;

    static bool IsNull(YamlNode node) => node is YamlScalarNode { Value: null or "" or "~" or "null", Style: not (YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted) };

    static string? PreviousOf(YamlMappingNode? context) =>
        context is not null && context.Children.TryGetValue(new YamlScalarNode("context"), out var body) && body is YamlMappingNode b
        && b.Children.TryGetValue(new YamlScalarNode("extensions"), out var list) && list is YamlSequenceNode extensions
            ? extensions.Children.OfType<YamlMappingNode>()
                .Where(e => Scalar(e, "name") == Extension)
                .Select(e => e.Children.TryGetValue(new YamlScalarNode("extension"), out var x) && x is YamlMappingNode m ? Scalar(m, PreviousContext) : null)
                .FirstOrDefault(p => p is not null)
            : null;

    static void Remember(YamlMappingNode context, string previous)
    {
        if (!context.Children.TryGetValue(new YamlScalarNode("context"), out var node) || node is not YamlMappingNode body)
            throw new InvalidDataException($"context {Scalar(context, "name")} has no body");
        var extensions = body.Children.TryGetValue(new YamlScalarNode("extensions"), out var list) && list is YamlSequenceNode s ? s : null;
        if (extensions is null)
        {
            extensions = [];
            body.Children[new YamlScalarNode("extensions")] = extensions;
        }
        foreach (var old in extensions.Children.OfType<YamlMappingNode>().Where(e => Scalar(e, "name") == Extension).ToList()) extensions.Children.Remove(old);
        extensions.Add(new YamlMappingNode(
            new YamlScalarNode("name"), new YamlScalarNode(Extension),
            new YamlScalarNode("extension"), new YamlMappingNode(new YamlScalarNode(PreviousContext), new YamlScalarNode(previous))));
    }
}
