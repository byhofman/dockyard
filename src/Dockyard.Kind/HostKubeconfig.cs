namespace Dockyard.Kind;

// The host's kubeconfig, which follows the clusters the dashboard creates and deletes. kind runs against a file of
// its own, so the user's is only ever written here.
sealed class HostKubeconfig(IReadOnlyList<string> paths)
{
    public static HostKubeconfig FromEnvironment() => new(KubeconfigPaths.FromEnvironment());

    public IReadOnlyList<string> Paths => paths;

    // Returns the file the entries went into.
    public string Add(string cluster, string kindKubeconfig)
    {
        var kind = KubeconfigFile.Parse(kindKubeconfig, "kind's kubeconfig");
        var path = KubeconfigPaths.ForMerge(paths, File.Exists);
        KubeconfigFile.Update(path, config =>
        {
            KindKubeconfig.Merge(config, kind, cluster);
            return true;
        });
        return path;
    }

    // Takes the cluster out of every file in the list, as kind delete does.
    public void Remove(string cluster)
    {
        foreach (var path in paths.Where(File.Exists))
            KubeconfigFile.Update(path, config => KindKubeconfig.Remove(config, cluster, name => ContextExists(name, path, config)));
    }

    // What kubectl sees across the files of the list: every context once, and the current one from the first file that names one.
    public (IReadOnlyList<string> Names, string? Current) Contexts()
    {
        List<string> names = [];
        string? current = null;
        foreach (var path in paths.Where(File.Exists))
        {
            YamlDotNet.RepresentationModel.YamlMappingNode config;
            try { config = KubeconfigFile.Read(path); }
            catch (InvalidDataException) { continue; }
            names.AddRange(KindKubeconfig.ContextNames(config).Where(n => !names.Contains(n)));
            current ??= KindKubeconfig.Current(config);
        }
        return (names, current);
    }

    // A kubeconfig of its own holding only the context, with its cluster and user taken from whichever file of the list has them and the
    // certificate files inlined, so it works without the files it came from. Readable by the person only; the caller deletes it.
    public string WriteContext(string context)
    {
        var configs = paths.Where(File.Exists).Select(p => (Path: p, Config: TryRead(p))).Where(f => f.Config is not null).ToList();
        var source = configs.FirstOrDefault(f => KindKubeconfig.Find(f.Config!, "contexts", context) is not null);
        if (source.Config is null) throw new ArgumentException($"There is no context named {context} in the kubeconfig");
        var entry = KindKubeconfig.Find(source.Config, "contexts", context)!;
        var body = entry.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("context"), out var b) && b is YamlDotNet.RepresentationModel.YamlMappingNode m
            ? m : throw new InvalidDataException($"context {context} has no body");
        var clusterName = KindKubeconfig.Scalar(body, "cluster");
        var userName = KindKubeconfig.Scalar(body, "user");

        var flat = KubeconfigFile.Parse("apiVersion: v1\nkind: Config\n", "the new kubeconfig");
        flat.Children[new YamlDotNet.RepresentationModel.YamlScalarNode("current-context")] = new YamlDotNet.RepresentationModel.YamlScalarNode(context);
        var clusters = Section(flat, "clusters");
        var users = Section(flat, "users");
        if (clusterName is not null) clusters.Add(Inlined(configs, "clusters", clusterName, "cluster", ["certificate-authority"]));
        if (userName is not null) users.Add(Inlined(configs, "users", userName, "user", ["client-certificate", "client-key"]));
        Section(flat, "contexts").Add(entry);

        var path = Path.Combine(Path.GetTempPath(), $"dockyard-ctx-{Guid.NewGuid():N}.yaml");
        var text = KubeconfigFile.Serialize(flat);
        try { PrivateFile.Write(path, text); }
        catch { File.Delete(path); throw; }
        return path;
    }

    static YamlDotNet.RepresentationModel.YamlSequenceNode Section(YamlDotNet.RepresentationModel.YamlMappingNode config, string name)
    {
        var list = new YamlDotNet.RepresentationModel.YamlSequenceNode();
        config.Children[new YamlDotNet.RepresentationModel.YamlScalarNode(name)] = list;
        return list;
    }

    static YamlDotNet.RepresentationModel.YamlMappingNode Inlined(
        List<(string Path, YamlDotNet.RepresentationModel.YamlMappingNode? Config)> configs, string list, string name, string body, string[] files)
    {
        foreach (var (path, config) in configs)
        {
            if (KindKubeconfig.Find(config!, list, name) is not { } found) continue;
            var copy = Clone(found);
            if (copy.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode(body), out var node) && node is YamlDotNet.RepresentationModel.YamlMappingNode inner)
            {
                var home = Path.GetDirectoryName(Path.GetFullPath(path))!;
                foreach (var key in files)
                {
                    if (KindKubeconfig.Scalar(inner, key) is not { } file) continue;
                    var full = Path.GetFullPath(file, home);
                    if (!File.Exists(full)) throw new FileNotFoundException($"{key} {full} of {name} in {path} does not exist");
                    inner.Children.Remove(new YamlDotNet.RepresentationModel.YamlScalarNode(key));
                    inner.Children[new YamlDotNet.RepresentationModel.YamlScalarNode(key + "-data")] =
                        new YamlDotNet.RepresentationModel.YamlScalarNode(Convert.ToBase64String(File.ReadAllBytes(full)));
                }
            }
            return copy;
        }
        throw new ArgumentException($"The kubeconfig has no {list.TrimEnd('s')} named {name}");
    }

    static YamlDotNet.RepresentationModel.YamlMappingNode Clone(YamlDotNet.RepresentationModel.YamlMappingNode node) =>
        (YamlDotNet.RepresentationModel.YamlMappingNode)KubeconfigFile.Parse(
            KubeconfigFile.Serialize(node), "a kubeconfig entry");

    static YamlDotNet.RepresentationModel.YamlMappingNode? TryRead(string path)
    {
        try { return KubeconfigFile.Read(path); }
        catch (InvalidDataException) { return null; }
    }

    // Like kubectl config use-context: the name has to exist in one of the files, and the current-context is written where kubectl
    // reads it from, the first file that has one, otherwise where new entries go.
    public void Use(string context)
    {
        if (!Contexts().Names.Contains(context)) throw new ArgumentException($"There is no context named {context} in the kubeconfig");
        var target = paths.Where(File.Exists).FirstOrDefault(p => HasCurrent(p)) ?? KubeconfigPaths.ForMerge(paths, File.Exists);
        KubeconfigFile.Update(target, config =>
        {
            if (KindKubeconfig.Current(config) == context) return false;
            KindKubeconfig.Use(config, context);
            return true;
        });
    }

    static bool HasCurrent(string path)
    {
        try { return KindKubeconfig.Current(KubeconfigFile.Read(path)) is not null; }
        catch (InvalidDataException) { return false; }
    }

    bool ContextExists(string name, string beingChanged, YamlDotNet.RepresentationModel.YamlMappingNode changed) =>
        KindKubeconfig.ContextNames(changed).Contains(name)
        || paths.Where(p => p != beingChanged && File.Exists(p)).Any(p =>
        {
            try { return KindKubeconfig.ContextNames(KubeconfigFile.Read(p)).Contains(name); }
            catch (InvalidDataException) { return false; }
        });
}
