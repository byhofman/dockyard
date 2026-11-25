using System.Diagnostics;
using Dockyard.Hosts;
using Dockyard.Kind;

namespace Dockyard.Core;

// dockyard cluster: kind clusters as the dashboard makes them, with the kubeconfig following along, their definitions
// kept, and the add-ons and shared storage it offers.
static class CliClusters
{
    public static async Task<int> Run(IEngineHost host, string verb, IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        var context = host.Engines.FirstOrDefault(e => e.Id == "linux")?.Context ?? throw new InvalidOperationException("kind needs the Linux engine, which is not here");
        var kind = CliLocator.Locate("kind") ?? throw new InvalidOperationException("kind is not installed: dockyard --silent --with kind adds it");
        var actions = new KindActions(kind, context, HostKubeconfig.FromEnvironment());
        string Name() => args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) is { } name && KindNames.IsValid(name)
            ? name : throw new Cli.UsageException("A cluster name is needed: lower case letters, digits, . and -");

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
                var addons = Addons(Cli.Option(args, "--addons"));
                var shared = args.Contains("--shared-storage");
                var workers = Cli.Option(args, "--workers") is { } w ? int.TryParse(w, out var n) && n is >= 0 and <= 10 ? n : throw new Cli.UsageException("--workers takes 0 to 10") : 0;
                var definition = Cli.Option(args, "--file") is not null ? Cli.Content(args) : KindCli.Config(workers, shared);
                if (ClusterDefinitions.Problem(definition) is { } problem) { error(problem); return 1; }
                var helm = addons.Count > 0 ? CliLocator.Locate("helm") ?? throw new InvalidOperationException("The add-ons go in with Helm, which is not installed: dockyard --silent --with helm adds it") : null;
                ClusterDefinitions.Save(name, new ClusterDefinition(definition, shared, addons.Select(a => a.Release).ToList()));
                var file = await actions.Create(name, log, sharedStorage: shared, addons: addons, helm: helm, definition: definition);
                log($"kubectl now uses {KindKubeconfig.Key(name)}, from {file}");
                return 0;
            }
            case "recreate":
            {
                var name = Name();
                var saved = ClusterDefinitions.Load(name) ?? throw new InvalidOperationException($"Dockyard has no definition of {name}: the dashboard puts one together from its nodes, under Definition");
                var definition = Cli.Option(args, "--file") is not null ? Cli.Content(args) : saved.Yaml;
                if (ClusterDefinitions.Problem(definition) is { } problem) { error(problem); return 1; }
                var helm = saved.Addons.Count > 0 ? CliLocator.Locate("helm") : null;
                await Delete(actions, context, name, log);
                var remade = saved with { Yaml = definition, Addons = helm is null ? [] : saved.Addons };
                ClusterDefinitions.Save(name, remade);
                await actions.Create(name, log, sharedStorage: remade.SharedStorage, addons: remade.AddonList, helm: helm, definition: remade.Yaml);
                log($"{name} is made again from its definition");
                return 0;
            }
            case "delete":
            {
                var name = Name();
                await Delete(actions, context, name, log);
                ClusterDefinitions.Remove(name);
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
            default: throw new Cli.UsageException($"Unknown cluster command: {verb}");
        }
    }

    static string Describe(ClusterDefinition d) =>
        string.Join(", ", new[] { d.SharedStorage ? "shared storage" : null }.Concat(d.AddonList.Select(a => a.Title)).OfType<string>()) is { Length: > 0 } extras ? extras : "-";

    static IReadOnlyList<ClusterAddon> Addons(string? names)
    {
        var wanted = (names ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = wanted.Where(w => ClusterAddons.All.All(a => a.Release != w)).ToList();
        if (unknown.Count > 0) throw new Cli.UsageException($"Unknown add-on: {string.Join(", ", unknown)}. Add-ons: {string.Join(", ", ClusterAddons.All.Select(a => a.Release))}");
        return ClusterAddons.All.Where(a => wanted.Contains(a.Release)).ToList();
    }

    // The cloud provider stops looking after a cluster that is gone, so its proxies would stay behind.
    static async Task Delete(KindActions actions, string context, string name, Action<string> log)
    {
        await actions.Delete(name, log);
        var ps = await Docker(["--context", context, "ps", "-aq", "--filter", $"label={KindLabels.LoadBalancerCluster}={name}"]);
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
