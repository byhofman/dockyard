using System.Diagnostics;
using Dockyard.Kind;

namespace Dockyard.Cli;

// dockyard cluster: kind clusters as the dashboard makes them, with the kubeconfig following along, their definitions
// kept, and the add-ons, storage and load balancer it offers.
sealed class Clusters(Action<string> log)
{
    // The docker context of the Linux engine Dockyard set up: its own on Windows and macOS, the system's on Linux.
    public static string Context => OperatingSystem.IsWindows() ? "linux" : OperatingSystem.IsMacOS() ? "dockyard" : "default";

    static string KindExe => CliLocator.Locate("kind") ?? throw new InvalidOperationException("kind is not installed: add it in the Dockyard Toolbox, or run dockyard --silent --with kind");

    static KindActions Actions() => new(KindExe, Context, HostKubeconfig.FromEnvironment());

    // kind runs only on the Linux engine: the Windows engine has no clusters, and kind's own error for an engine that is
    // not there says little.
    static async Task RequireLinuxEngine()
    {
        var (exit, output, _) = await SharedStorage.Docker(["--context", Context, "version", "--format", "{{.Server.Os}}"]);
        if (exit == 0 && output.Trim() == "linux") return;
        throw new InvalidOperationException(OperatingSystem.IsWindows()
            ? "kind runs on the Linux engine, which is not running: dockyard engine start linux"
            : "kind runs on the Linux engine, which is not running: dockyard engine start");
    }

    public static string ValidName(string? name) => name is not null && KindNames.IsValid(name)
        ? name : throw new UsageException("A cluster name is needed: lower case letters, digits, . and -");

    public static ClusterStorage Storage(string? storage) => storage switch
    {
        null or "none" => ClusterStorage.None,
        "host-folder" => ClusterStorage.HostFolder,
        "nfs" => ClusterStorage.Nfs,
        _ => throw new UsageException($"Unknown storage: {storage}. Storage: none, host-folder, nfs"),
    };

    public static IReadOnlyList<int> Ports(string? ports) => ports is null ? ClusterDefinition.DefaultPorts
        : ClusterDefinitions.ParsePorts(ports) ?? throw new UsageException("--ports takes a list of ports, such as 80,443");

    public static IReadOnlyList<ClusterAddon> Addons(string? names)
    {
        var wanted = (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = wanted.Where(w => ClusterAddons.All.All(a => a.Release != w)).ToList();
        if (unknown.Count > 0) throw new UsageException($"Unknown add-on: {string.Join(", ", unknown)}. Add-ons: {string.Join(", ", ClusterAddons.All.Select(a => a.Release))}");
        return ClusterAddons.All.Where(a => wanted.Contains(a.Release)).ToList();
    }

    public async Task<IReadOnlyList<(string Name, string Details)>> List()
    {
        await RequireLinuxEngine();
        var r = await KindCli.Run(KindExe, Context, KindCli.Clusters);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Error.Trim());
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !n.StartsWith("No kind clusters", StringComparison.Ordinal))
            .Select(n => (n, ClusterDefinitions.Load(n) is { } d ? Describe(d) : "made elsewhere"))
            .ToList();
    }

    public async Task Create(string name, int workers, IReadOnlyList<ClusterAddon> addons, ClusterStorage storage, bool loadBalancer, IReadOnlyList<int> ports, string? definition)
    {
        await RequireLinuxEngine();
        if (workers is < 0 or > 10) throw new UsageException("--workers takes 0 to 10");
        definition ??= KindCli.Config(workers, storage == ClusterStorage.HostFolder);
        if (ClusterDefinitions.Problem(definition) is { } problem) throw new InvalidOperationException(problem);
        var helm = addons.Count > 0 || storage == ClusterStorage.Nfs
            ? CliLocator.Locate("helm") ?? throw new InvalidOperationException("The add-ons and NFS storage go in with Helm, which is not installed: dockyard --silent --with helm adds it")
            : null;
        ClusterDefinitions.Save(name, new ClusterDefinition(definition, storage, addons.Select(a => a.Release).ToList(), loadBalancer, ports));
        var file = await Actions().Create(name, log, storage: storage, addons: addons, helm: helm, definition: definition, publishNames: loadBalancer, clusterCa: ClusterCa);
        if (loadBalancer) await CloudProvider.EnsureRunning(Context.Length > 0 ? Context : "default", log);
        log($"kubectl now uses {KindKubeconfig.Key(name)}, from {file}");
    }

    static Task<string> ClusterCa(string cluster) =>
        CertManager.FromToolbox(Toolbox.Locate() ?? throw new InvalidOperationException("The Dockyard Toolbox is not installed next to this command"), cluster);

    public async Task Recreate(string name, string? definition)
    {
        await RequireLinuxEngine();
        var saved = ClusterDefinitions.Load(name) ?? throw new InvalidOperationException($"Dockyard has no definition of {name}: the dashboard puts one together from its nodes, under Definition");
        definition ??= saved.Yaml;
        if (ClusterDefinitions.Problem(definition) is { } problem) throw new InvalidOperationException(problem);
        var helm = saved.Addons.Count > 0 || saved.Storage == ClusterStorage.Nfs ? CliLocator.Locate("helm") : null;
        var actions = Actions();
        await DeleteWithProxies(actions, name);
        var remade = saved with { Yaml = definition, Addons = helm is null ? [] : saved.Addons };
        ClusterDefinitions.Save(name, remade);
        await actions.Create(name, log, storage: remade.Storage, addons: remade.AddonList, helm: helm, definition: remade.Yaml, publishNames: remade.LoadBalancer, clusterCa: ClusterCa);
        if (remade.LoadBalancer) await CloudProvider.EnsureRunning(Context.Length > 0 ? Context : "default", log);
        log($"{name} is made again from its definition");
    }

    // A running cluster's add-ons and NFS storage, as the dashboard's Add-ons does; the rest is its nodes' and needs recreate.
    public async Task Edit(string name, string? addons, string? storage)
    {
        await RequireLinuxEngine();
        var saved = ClusterDefinitions.Load(name) ?? throw new InvalidOperationException($"Dockyard has no definition of {name}: the dashboard puts one together from its nodes, under Definition");
        var helm = CliLocator.Locate("helm") ?? throw new InvalidOperationException("The add-ons go in with Helm, which is not installed: dockyard --silent --with helm adds it");
        var wanted = addons is null ? saved.AddonList : Addons(addons);
        var wantedStorage = storage is null ? saved.Storage : Storage(storage);
        if (wantedStorage != saved.Storage && (wantedStorage == ClusterStorage.HostFolder || saved.Storage == ClusterStorage.HostFolder))
            throw new InvalidOperationException("The shared host folder is mounted on each node when kind makes it: change it with dockyard cluster recreate and --file");
        bool? nfs = wantedStorage == saved.Storage ? null : wantedStorage == ClusterStorage.Nfs;
        await Actions().EditAddons(name, saved.AddonList, wanted, helm, ClusterCa, log, nfs);
        ClusterDefinitions.Save(name, saved with { Addons = wanted.Select(a => a.Release).ToList(), Storage = wantedStorage });
        log($"{name} is changed");
    }

    public async Task DashboardToken(string name)
    {
        await RequireLinuxEngine();
        log(await Headlamp.Token(Context, name));
    }

    public async Task Delete(string name)
    {
        await RequireLinuxEngine();
        await DeleteWithProxies(Actions(), name);
        ClusterDefinitions.Remove(name);
        var left = await List();
        await NfsStorage.RemoveServerIfUnused(Context, left.Select(c => ClusterDefinitions.Load(c.Name)));
        log($"Deleted {name}");
    }

    public async Task Use(string name)
    {
        await RequireLinuxEngine();
        log($"kubectl now uses {KindKubeconfig.Key(name)}, from {await Actions().UseContext(name)}");
    }

    public async Task Kubeconfig(string name) => log((await Actions().Kubeconfig(name)).TrimEnd());

    public void Definition(string name) =>
        log((ClusterDefinitions.Load(name) ?? throw new InvalidOperationException($"Dockyard has no definition of {name}")).Yaml.TrimEnd());

    static string Describe(ClusterDefinition d) =>
        string.Join(", ", new[]
        {
            d.Storage switch { ClusterStorage.HostFolder => "shared host folder", ClusterStorage.Nfs => "NFS storage", _ => null },
            d.LoadBalancer ? $"ports {string.Join(',', d.LoadBalancerPorts)}" : null,
        }.Concat(d.AddonList.Select(a => a.Title)).OfType<string>()) is { Length: > 0 } extras ? extras : "-";

    // The cloud provider stops looking after a cluster that is gone, so its proxies would stay behind, and the forwarder too.
    async Task DeleteWithProxies(KindActions actions, string name)
    {
        await actions.Delete(name, log);
        var ps = await Docker(["--context", Context, "ps", "-aq", "--filter", $"label={KindLabels.LoadBalancerCluster}={name}"])
            + "\n" + await Docker(["--context", Context, "ps", "-aq", "--filter", $"label={KindLabels.ForwarderCluster}={name}"]);
        var proxies = ps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (proxies.Length > 0) await Docker(["--context", Context, "rm", "-f", .. proxies]);
    }

    static async Task<string> Docker(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(DockerCli.Path()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start docker");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
