namespace Dockyard.Dashboard.Features.Clusters;

// What the Clusters section does with kind, with the host's kubeconfig following along.
sealed class KindActions(string kind, string dockerContext, HostKubeconfig host)
{
    // Returns the kubeconfig file the cluster's context went into.
    // Add-ons go in with helm once the cluster is in the kubeconfig, so a chart that fails leaves a cluster kubectl can reach.
    public async Task<string> Create(string name, Action<string>? onLine = null, int workers = 0, bool sharedStorage = false,
        IReadOnlyList<ClusterAddon>? addons = null, string? helm = null, string? definition = null)
    {
        var scratch = Directory.CreateTempSubdirectory("dockyard-kind-");
        try
        {
            var own = Path.Combine(scratch.FullName, "config");
            string? config = null;
            if (definition is not null || workers > 0 || sharedStorage)
            {
                config = Path.Combine(scratch.FullName, "cluster.yaml");
                await File.WriteAllTextAsync(config, definition ?? KindCli.Config(workers, sharedStorage));
            }
            Check(await KindCli.Run(kind, dockerContext, KindCli.Create(name, own, config), onLine));
            if (sharedStorage) await SharedStorage.Install(dockerContext.Length > 0 ? dockerContext : "default", name, onLine);
            var text = await File.ReadAllTextAsync(own);
            var file = await Into(name, text, "was created");
            foreach (var addon in addons ?? [])
                await ClusterAddons.Install(helm ?? throw new InvalidOperationException("Helm is not installed."), addon, own, name, Path.Combine(scratch.FullName, "helm"), onLine);
            return file;
        }
        finally { scratch.Delete(recursive: true); }
    }

    public async Task Delete(string name, Action<string>? onLine = null)
    {
        var scratch = Directory.CreateTempSubdirectory("dockyard-kind-");
        try { Check(await KindCli.Run(kind, dockerContext, KindCli.Delete(name, Path.Combine(scratch.FullName, "config")), onLine)); }
        finally { scratch.Delete(recursive: true); }
        try { await Task.Run(() => host.Remove(name)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{name} was deleted, but its entries are still in the kubeconfig: {ex.Message}");
        }
    }

    // Puts the cluster back in the kubeconfig if it went missing, and makes its context the current one.
    public async Task<string> UseContext(string name) => await Into(name, await Kubeconfig(name), "runs");

    public async Task<string> Kubeconfig(string name)
    {
        var result = await KindCli.Run(kind, dockerContext, KindCli.Kubeconfig(name));
        Check(result);
        return result.Output;
    }

    async Task<string> Into(string name, string kindKubeconfig, string state)
    {
        try { return await Task.Run(() => host.Add(name, kindKubeconfig)); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{name} {state}, but the kubeconfig was left as it is: {ex.Message}");
        }
    }

    static void Check((int ExitCode, string Output, string Error) result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.Error.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"kind exited with {result.ExitCode}");
    }
}
