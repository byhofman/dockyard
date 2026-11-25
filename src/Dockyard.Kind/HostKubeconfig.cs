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

    bool ContextExists(string name, string beingChanged, YamlDotNet.RepresentationModel.YamlMappingNode changed) =>
        KindKubeconfig.ContextNames(changed).Contains(name)
        || paths.Where(p => p != beingChanged && File.Exists(p)).Any(p =>
        {
            try { return KindKubeconfig.ContextNames(KubeconfigFile.Read(p)).Contains(name); }
            catch (InvalidDataException) { return false; }
        });
}
