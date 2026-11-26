using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dockyard.Kind;

// How a cluster was made, so it can be made again: kind keeps no copy of the config it was given.
enum ClusterStorage { None, HostFolder, Nfs }

// The load balancer gives LoadBalancer services an address; Ports are the host ports on 127.0.0.1 its services get.
sealed record ClusterDefinition(string Yaml, ClusterStorage Storage, IReadOnlyList<string> Addons, bool LoadBalancer = true, IReadOnlyList<int>? Ports = null, string? KubernetesVersion = null)
{
    // None: <host>.<cluster>.dockyard.internal reaches a cluster's ingress by name on 80 and 443, which fixed ports would take.
    public static readonly IReadOnlyList<int> DefaultPorts = [];

    public bool SharedStorage => Storage == ClusterStorage.HostFolder;
    public IReadOnlyList<int> LoadBalancerPorts => LoadBalancer ? Ports ?? DefaultPorts : [];
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
        var storage = Value("storage") switch
        {
            "host-folder" => ClusterStorage.HostFolder,
            "nfs" => ClusterStorage.Nfs,
            _ => Value("shared-storage") == "true" ? ClusterStorage.HostFolder : ClusterStorage.None,
        };
        return new ClusterDefinition(
            File.ReadAllText(YamlFile(folder, name)),
            storage,
            (Value("addons") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
            Value("load-balancer") != "false",
            ParsePorts(Value("ports") ?? "") ?? ClusterDefinition.DefaultPorts,
            Value("kubernetes") is { Length: > 0 } version ? version : null);
    }

    public static void Save(string name, ClusterDefinition definition, string? folder = null)
    {
        folder ??= Folder;
        Directory.CreateDirectory(folder);
        File.WriteAllText(YamlFile(folder, name), definition.Yaml);
        File.WriteAllLines(MetaFile(folder, name),
        [
            $"storage={StorageKey(definition.Storage)}",
            $"addons={string.Join(',', definition.Addons)}",
            $"load-balancer={(definition.LoadBalancer ? "true" : "false")}",
            $"ports={string.Join(',', definition.Ports ?? ClusterDefinition.DefaultPorts)}",
            $"kubernetes={definition.KubernetesVersion}",
        ]);
    }

    public static string StorageKey(ClusterStorage storage) => storage switch
    {
        ClusterStorage.HostFolder => "host-folder",
        ClusterStorage.Nfs => "nfs",
        _ => "none",
    };

    // "80, 443" as ports, nothing as none; null when it is not a list of distinct ports.
    public static IReadOnlyList<int>? ParsePorts(string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        var ports = new List<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var port) || port is < 1 or > 65535 || ports.Contains(port)) return null;
            ports.Add(port);
        }
        return ports;
    }

    public static IReadOnlyList<string> Names(string? folder = null)
    {
        folder ??= Folder;
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.yaml").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToList()
            : [];
    }

    // What the engine's agent keeps running for each cluster.
    public static List<Engine.ClusterWant> Wants(string? folder = null) => Names(folder)
        .Select(name => (name, Definition: Load(name, folder)))
        .Where(d => d.Definition is not null)
        .Select(d => new Engine.ClusterWant(d.name, d.Definition!.LoadBalancer, d.Definition.LoadBalancerPorts, d.Definition.Storage == ClusterStorage.Nfs))
        .ToList();

    public static void Remove(string name, string? folder = null)
    {
        folder ??= Folder;
        File.Delete(YamlFile(folder, name));
        File.Delete(MetaFile(folder, name));
    }

    // A cluster made elsewhere, or before Dockyard kept definitions: its nodes are what its config said, near enough.
    public static ClusterDefinition Guess(KindCluster cluster) => new(
        KindCli.Config(cluster.Nodes.Count(n => n.Role == "worker"), cluster.HasSharedStorage),
        cluster.HasSharedStorage ? ClusterStorage.HostFolder : ClusterStorage.None, [], KubernetesVersion: cluster.KubernetesVersion);

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
