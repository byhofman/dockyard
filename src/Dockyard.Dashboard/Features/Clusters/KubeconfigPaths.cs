namespace Dockyard.Dashboard.Features.Clusters;

// The files kubectl and kind read, chosen the way both of them choose.
static class KubeconfigPaths
{
    public static IReadOnlyList<string> All(string? kubeconfigVariable, string home)
    {
        var listed = (kubeconfigVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        return listed.Count > 0 ? listed : [Path.Combine(home, ".kube", "config")];
    }

    // New entries go into the first file of the list that exists, or the last one when none does.
    public static string ForMerge(IReadOnlyList<string> paths, Func<string, bool> exists) =>
        paths.Count == 1 ? paths[0] : paths.FirstOrDefault(exists) ?? paths[^1];

    public static IReadOnlyList<string> FromEnvironment() =>
        All(Environment.GetEnvironmentVariable("KUBECONFIG"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
