using System.Diagnostics;
using Dockyard.Kind;

namespace Dockyard.Cli;

// dockyard cluster: kind clusters as the dashboard makes them, with the kubeconfig following along, their definitions
// kept, and the add-ons, storage and load balancer it offers.
static class Clusters
{
    // The docker context of the Linux engine Dockyard set up: its own on Windows and macOS, the system's on Linux.
    public static string Context => OperatingSystem.IsWindows() ? "linux" : OperatingSystem.IsMacOS() ? "dockyard" : "default";

    public static async Task<int> Run(string verb, IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        var context = Context;
        var kind = CliLocator.Locate("kind") ?? throw new InvalidOperationException("kind is not installed: add it in the Dockyard Toolbox, or run dockyard --silent --with kind");
        var actions = new KindActions(kind, context, HostKubeconfig.FromEnvironment());
        string Name() => args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) is { } name && KindNames.IsValid(name)
            ? name : throw new UsageException("A cluster name is needed: lower case letters, digits, . and -");

        switch (verb)
        {
            case "ls":
            {
                var r = await KindCli.Run(kind, context, KindCli.Clusters);
                if (r.ExitCode != 0) throw new InvalidOperationException(r.Error.Trim());
                foreach (var name in r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (name.StartsWith("No kind clusters", StringComparison.Ordinal)) continue;
                    log(ClusterDefinitions.Load(name) is { } d ? $"{name}\t{Describe(d)}" : $"{name}\tmade elsewhere");
                }
                return 0;
            }
            case "create":
            {
                var name = Name();
                var addons = Addons(Arguments.Option(args, "--addons"));
                var storage = (Arguments.Option(args, "--storage") ?? (args.Contains("--shared-storage") ? "host-folder" : "none")) switch
                {
                    "none" => ClusterStorage.None,
                    "host-folder" => ClusterStorage.HostFolder,
                    "nfs" => ClusterStorage.Nfs,
                    var other => throw new UsageException($"Unknown storage: {other}. Storage: none, host-folder, nfs"),
                };
                var ports = Arguments.Option(args, "--ports") is { } p ? ClusterDefinitions.ParsePorts(p) ?? throw new UsageException("--ports takes a list of ports, such as 80,443") : ClusterDefinition.DefaultPorts;
                var workers = Arguments.Option(args, "--workers") is { } w ? int.TryParse(w, out var n) && n is >= 0 and <= 10 ? n : throw new UsageException("--workers takes 0 to 10") : 0;
                var definition = Arguments.Option(args, "--file") is not null ? Arguments.Content(args) : KindCli.Config(workers, storage == ClusterStorage.HostFolder);
                if (ClusterDefinitions.Problem(definition) is { } problem) { error(problem); return 1; }
                var helm = addons.Count > 0 || storage == ClusterStorage.Nfs ? CliLocator.Locate("helm") ?? throw new InvalidOperationException("The add-ons go in with Helm, which is not installed: dockyard --silent --with helm adds it") : null;
                ClusterDefinitions.Save(name, new ClusterDefinition(definition, storage, addons.Select(a => a.Release).ToList(), !args.Contains("--no-load-balancer"), ports));
                var file = await actions.Create(name, log, storage: storage, addons: addons, helm: helm, definition: definition);
                log($"kubectl now uses {KindKubeconfig.Key(name)}, from {file}");
                return 0;
            }
            case "recreate":
            {
                var name = Name();
                var saved = ClusterDefinitions.Load(name) ?? throw new InvalidOperationException($"Dockyard has no definition of {name}: the dashboard puts one together from its nodes, under Definition");
                var definition = Arguments.Option(args, "--file") is not null ? Arguments.Content(args) : saved.Yaml;
                if (ClusterDefinitions.Problem(definition) is { } problem) { error(problem); return 1; }
                var helm = saved.Addons.Count > 0 || saved.Storage == ClusterStorage.Nfs ? CliLocator.Locate("helm") : null;
                await Delete(actions, context, name, log);
                var remade = saved with { Yaml = definition, Addons = helm is null ? [] : saved.Addons };
                ClusterDefinitions.Save(name, remade);
                await actions.Create(name, log, storage: remade.Storage, addons: remade.AddonList, helm: helm, definition: remade.Yaml);
                log($"{name} is made again from its definition");
                return 0;
            }
            case "delete":
            {
                var name = Name();
                await Delete(actions, context, name, log);
                ClusterDefinitions.Remove(name);
                var left = (await KindCli.Run(kind, context, KindCli.Clusters)).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                await NfsStorage.RemoveServerIfUnused(context, left.Select(c => ClusterDefinitions.Load(c)));
                log($"Deleted {name}");
                return 0;
            }
            case "use":
                log($"kubectl now uses {KindKubeconfig.Key(Name())}, from {await actions.UseContext(Name())}");
                return 0;
            case "kubeconfig":
                log((await actions.Kubeconfig(Name())).TrimEnd());
                return 0;
            case "definition":
            {
                var name = Name();
                log((ClusterDefinitions.Load(name) ?? throw new InvalidOperationException($"Dockyard has no definition of {name}")).Yaml.TrimEnd());
                return 0;
            }
            default: throw new UsageException($"Unknown cluster command: {verb}");
        }
    }

    static string Describe(ClusterDefinition d) =>
        string.Join(", ", new[] { d.Storage switch { ClusterStorage.HostFolder => "shared host folder", ClusterStorage.Nfs => "NFS storage", _ => null }, d.LoadBalancer ? $"ports {string.Join(',', d.LoadBalancerPorts)}" : null }.Concat(d.AddonList.Select(a => a.Title)).OfType<string>()) is { Length: > 0 } extras ? extras : "-";


    static IReadOnlyList<ClusterAddon> Addons(string? names)
    {
        var wanted = (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = wanted.Where(w => ClusterAddons.All.All(a => a.Release != w)).ToList();
        if (unknown.Count > 0) throw new UsageException($"Unknown add-on: {string.Join(", ", unknown)}. Add-ons: {string.Join(", ", ClusterAddons.All.Select(a => a.Release))}");
        return ClusterAddons.All.Where(a => wanted.Contains(a.Release)).ToList();
    }

    // The cloud provider stops looking after a cluster that is gone, so its proxies would stay behind.
    static async Task Delete(KindActions actions, string context, string name, Action<string> log)
    {
        await actions.Delete(name, log);
        var ps = await Docker(["--context", context, "ps", "-aq", "--filter", $"label={KindLabels.LoadBalancerCluster}={name}"])
            + "\n" + await Docker(["--context", context, "ps", "-aq", "--filter", $"label={KindLabels.ForwarderCluster}={name}"]);
        var proxies = ps.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (proxies.Length > 0) await Docker(["--context", context, "rm", "-f", .. proxies]);
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
