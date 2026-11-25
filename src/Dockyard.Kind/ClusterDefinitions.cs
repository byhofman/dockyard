using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockyard.Kind;

// How a cluster was made, so it can be made again: kind keeps no copy of the config it was given.
sealed record ClusterDefinition(string Yaml, bool SharedStorage, IReadOnlyList<string> Addons)
{
    public IReadOnlyList<ClusterAddon> AddonList => ClusterAddons.All.Where(a => Addons.Contains(a.Release)).ToList();
}

static class ClusterDefinitions
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "clusters");

    static string YamlFile(string folder, string name) => Path.Combine(folder, name + ".yaml");
    static string MetaFile(string folder, string name) => Path.Combine(folder, name + ".meta");

    public static ClusterDefinition? Load(string name, string? folder = null)
    {
        folder ??= Folder;
        if (!File.Exists(YamlFile(folder, name))) return null;
        var meta = File.Exists(MetaFile(folder, name)) ? File.ReadAllLines(MetaFile(folder, name)) : [];
        string? Value(string key) => meta.Select(l => l.Split('=', 2)).FirstOrDefault(p => p.Length == 2 && p[0] == key)?[1];
        return new ClusterDefinition(
            File.ReadAllText(YamlFile(folder, name)),
            Value("shared-storage") == "true",
            (Value("addons") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    public static void Save(string name, ClusterDefinition definition, string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(YamlFile(folder, name), definition.Yaml);
        File.WriteAllLines(MetaFile(folder, name), [$"shared-storage={(definition.SharedStorage ? "true" : "false")}", $"addons={string.Join(',', definition.Addons)}"]);
    }

    public static void Remove(string name, string? folder = null)
    {
        folder ??= Folder;
        File.Delete(YamlFile(folder, name));
        File.Delete(MetaFile(folder, name));
    }

    // A cluster made elsewhere, or before Dockyard kept definitions: its nodes are what its config said, near enough.
    public static ClusterDefinition Guess(KindCluster cluster) => new(
        KindCli.Config(cluster.Nodes.Count(n => n.Role == "worker"), cluster.HasSharedStorage),
        cluster.HasSharedStorage, []);

    // Null when kind can take it as far as can be told without kind: a v1alpha4 Cluster with a control plane.
    public static string? Problem(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlException e) { return $"Line {e.Start.Line}, column {e.Start.Column}: {e.Message.Split(". ")[0].TrimEnd('.')}"; }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) return "The definition has to be one YAML mapping";
        string? Scalar(string key) => root.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;
        if (Scalar("kind") != "Cluster") return "kind: Cluster is missing";
        if (Scalar("apiVersion") != "kind.x-k8s.io/v1alpha4") return "apiVersion has to be kind.x-k8s.io/v1alpha4";
        if (!root.Children.TryGetValue(new YamlScalarNode("nodes"), out var nodes)) return null;
        if (nodes is not YamlSequenceNode list) return "nodes has to be a list";
        var roles = list.Children.OfType<YamlMappingNode>()
            .Select(n => n.Children.TryGetValue(new YamlScalarNode("role"), out var r) && r is YamlScalarNode s ? s.Value : null).ToList();
        if (!roles.Contains("control-plane")) return "nodes needs a control-plane";
        return roles.FirstOrDefault(r => r is not ("control-plane" or "worker")) is { } other ? $"Unknown node role: {other}" : null;
    }
}
