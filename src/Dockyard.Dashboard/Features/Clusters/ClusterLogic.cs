using System.Diagnostics;
using System.Text.RegularExpressions;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Clusters;

sealed record KindNode(string Name, string Role, string State)
{
    public bool IsRunning => State == "running";
}

sealed record KindCluster(string Name, IReadOnlyList<KindNode> Nodes, string? KubernetesVersion, int? ApiPort)
{
    public string Context => "kind-" + Name;
    public bool IsRunning => Nodes.All(n => n.IsRunning);
    public string StateText => IsRunning ? "Running" : Nodes.Any(n => n.IsRunning) ? "Partly stopped" : "Stopped";
    public string NodesText => Nodes.Count == 1 ? "1 node" : $"{Nodes.Count} nodes";
    public string? ApiServer => IsRunning && ApiPort is { } port ? $"https://127.0.0.1:{port}" : null;
    public string Summary => string.Join("  ·  ", new[] { KubernetesVersion, NodesText, StateText }.Where(s => s is not null));
}

// kind keeps no state of its own: a cluster is the containers carrying its labels, so the engine's list is the truth.
static partial class KindClusters
{
    public const string ClusterLabel = "io.x-k8s.kind.cluster";
    public const string RoleLabel = "io.x-k8s.kind.role";
    const int ApiServerPort = 6443;

    public static List<KindCluster> From(IEnumerable<ContainerItem> containers) => containers
        .Where(c => c.Labels.ContainsKey(ClusterLabel))
        .GroupBy(c => c.Labels[ClusterLabel])
        .Select(g =>
        {
            var nodes = g.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            string Role(ContainerItem c) => c.Labels.GetValueOrDefault(RoleLabel, "");
            // With several control planes kind puts a load balancer in front, and that is the address in the kubeconfig.
            var front = nodes.FirstOrDefault(c => Role(c) == "external-load-balancer") ?? nodes.FirstOrDefault(c => Role(c) == "control-plane");
            var port = front?.Ports.FirstOrDefault(p => p.ContainerPort == ApiServerPort)?.HostPort;
            var version = nodes.Where(c => Role(c).StartsWith("control-plane", StringComparison.Ordinal))
                .Select(c => NodeVersion().Match(c.Image)).FirstOrDefault(m => m.Success)?.Groups["v"].Value;
            return new KindCluster(g.Key, nodes.Select(c => new KindNode(c.Name, Role(c), c.State)).ToList(), version, port);
        })
        .OrderBy(c => c.Name, StringComparer.Ordinal)
        .ToList();

    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    [GeneratedRegex(@"^[a-z0-9.-]+$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^(?:[^@]*/)?node:(?<v>v\d+\.\d+\.\d+)")]
    private static partial Regex NodeVersion();
}

static class KindCli
{
    static string Exe => OperatingSystem.IsWindows() ? "kind.exe" : "kind";

    // Where Dockyard installs it on each system.
    public static string OwnPath(string home) =>
        OperatingSystem.IsWindows() ? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Docker", "kind.exe")
        : OperatingSystem.IsMacOS() ? System.IO.Path.Combine(home, ".dockyard", "bin", "kind")
        : "/usr/local/bin/kind";

    public static string? Locate() =>
        Find(File.Exists, System.Environment.GetEnvironmentVariable("PATH"), System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

    // An app started from the menu bar or at login does not get a shell's PATH, so Homebrew's folders are looked at too.
    public static string? Find(Func<string, bool> exists, string? path, string home)
    {
        IEnumerable<string> dirs = (path ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        if (OperatingSystem.IsMacOS()) dirs = dirs.Concat(["/opt/homebrew/bin", "/usr/local/bin"]);
        return new[] { OwnPath(home) }.Concat(dirs.Select(d => System.IO.Path.Combine(d, Exe))).FirstOrDefault(exists);
    }

    // kind calls the docker CLI: it has to find the same one, and use the engine the dashboard shows.
    public static Dictionary<string, string> Environment(string context, string dockerCli, string? path)
    {
        var dir = System.IO.Path.GetDirectoryName(dockerCli);
        return new()
        {
            ["DOCKER_CONTEXT"] = context,
            ["KIND_EXPERIMENTAL_PROVIDER"] = "docker",
            ["PATH"] = string.IsNullOrEmpty(dir) ? path ?? "" : dir + System.IO.Path.PathSeparator + path,
        };
    }

    public static string[] Create(string name) => ["create", "cluster", "--name", name];
    public static string[] Delete(string name) => ["delete", "cluster", "--name", name];
    public static string[] Kubeconfig(string name) => ["get", "kubeconfig", "--name", name];
    // Merges the cluster into ~/.kube/config and makes it the current context, as kind create does.
    public static string[] UseContext(string name) => ["export", "kubeconfig", "--name", name];

    public static async Task<(int ExitCode, string Output, string Error)> Run(string kind, string context, IReadOnlyList<string> args, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(kind)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in Environment(context, Containers.DockerCli.Path(), System.Environment.GetEnvironmentVariable("PATH")))
            psi.Environment[key] = value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start kind");
        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.AppendLine(e.Data); };
        // kind reports progress on stderr.
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            error.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return (process.ExitCode, output.ToString(), error.ToString());
    }
}
