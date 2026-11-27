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
