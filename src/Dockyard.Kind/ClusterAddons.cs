using System.Diagnostics;

namespace Dockyard.Kind;

// A Helm chart the dashboard can put in a new cluster, at a pinned version like everything else Dockyard installs.
sealed record ClusterAddon(string Title, string Release, string Chart, string Repo, string Version, string Namespace, IReadOnlyList<string> Values);

static class ClusterAddons
{
    // Its LoadBalancer service gets an address and a port on this computer from kind's cloud provider, as any other does.
    public static readonly ClusterAddon Traefik =
        new("Traefik", "traefik", "traefik", "https://traefik.github.io/charts", "43.0.0", "traefik", []);

    // kind's kubelets serve certificates without their IP address in them, so metrics-server cannot verify them.
    public static readonly ClusterAddon MetricsServer =
        new("metrics-server", "metrics-server", "metrics-server", "https://kubernetes-sigs.github.io/metrics-server/", "3.14.0", "kube-system", ["args[0]=--kubelet-insecure-tls"]);

    public static readonly IReadOnlyList<ClusterAddon> All = [MetricsServer, Traefik, CertManager.Addon, VictoriaMetrics.Addon, Headlamp.Addon];

    // Versions are pinned, so a chart once downloaded never changes: kept here, an install reads no repository index,
    // which for some repositories is megabytes, and works offline.
    public static string ChartCache => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockyard", "charts");

    public static string ChartFile(ClusterAddon addon, string cache) => Path.Combine(cache, $"{addon.Chart}-{addon.Version}.tgz");

    // Helm gets repositories of its own in scratch: with --repo it still reads the user's list, and fails on a repository
    // there whose cache is gone.
    public static string[] PullArgs(ClusterAddon addon, string destination, string scratch) =>
    [
        "pull", addon.Chart, "--repo", addon.Repo, "--version", addon.Version, "--destination", destination,
        "--repository-config", Path.Combine(scratch, "repositories.yaml"), "--repository-cache", Path.Combine(scratch, "cache"),
    ];

    // kind's own kubeconfig, never the user's: its context for the cluster is kind-<name>.
    public static string[] InstallArgs(ClusterAddon addon, string chartFile, string kubeconfig, string cluster) =>
    [
        "upgrade", "--install", addon.Release, chartFile,
        "--namespace", addon.Namespace, "--create-namespace", "--kubeconfig", kubeconfig, "--kube-context", KindKubeconfig.Key(cluster),
        .. addon.Values.SelectMany(v => new[] { "--set", v }),
    ];

    // The chart file, downloaded first when it is not in the cache yet. Pulled into a folder of its own and moved in
    // whole, so a CLI and a dashboard fetching the same chart never see half a file.
    public static async Task<string> Fetch(string helm, ClusterAddon addon, string scratch, Action<string>? onLine, string? cache = null)
    {
        cache ??= ChartCache;
        var file = ChartFile(addon, cache);
        if (File.Exists(file)) return file;
        Directory.CreateDirectory(cache);
        var into = Directory.CreateTempSubdirectory("dockyard-chart-");
        try
        {
            onLine?.Invoke($"Downloading {addon.Title} {addon.Version}");
            var (exit, error) = await Helm(helm, PullArgs(addon, into.FullName, Path.Combine(scratch, addon.Release)), null);
            if (exit != 0) throw new InvalidOperationException($"{addon.Title}'s chart could not be downloaded: {LastLine(error) ?? $"helm exited with {exit}"}");
            var pulled = into.GetFiles("*.tgz").SingleOrDefault() ?? throw new InvalidOperationException($"helm pull left no chart for {addon.Title}");
            File.Move(pulled.FullName, file, overwrite: true);
            return file;
        }
        finally { into.Delete(recursive: true); }
    }

    // Every chart that is not cached yet, at once. It never throws: a chart that fails here is tried again by its install,
    // which then says why.
    public static async Task Prefetch(string helm, IEnumerable<ClusterAddon> addons, string scratch, Action<string>? onLine, string? cache = null)
    {
        var missing = addons.Distinct().Where(a => !File.Exists(ChartFile(a, cache ?? ChartCache))).ToList();
        if (missing.Count == 0) return;
        await Task.WhenAll(missing.Select(async a =>
        {
            try { await Fetch(helm, a, scratch, onLine, cache); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        }));
    }

    public static async Task Uninstall(string helm, ClusterAddon addon, string kubeconfig, string cluster, Action<string>? onLine)
    {
        onLine?.Invoke($"Removing {addon.Title}");
        var psi = new ProcessStartInfo(helm) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "uninstall", addon.Release, "--namespace", addon.Namespace, "--kubeconfig", kubeconfig, "--kube-context", KindKubeconfig.Key(cluster), "--ignore-not-found" })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start helm");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{addon.Title} was not removed: {error.Trim()}");
    }

    public static async Task Install(string helm, ClusterAddon addon, string kubeconfig, string cluster, string scratch, Action<string>? onLine, string? step = null)
    {
        var chart = await Fetch(helm, addon, scratch, onLine);
        onLine?.Invoke($"Installing {addon.Title} {addon.Version} with Helm{(step is null ? "" : $" ({step})")}");
        var (exit, error) = await Helm(helm, InstallArgs(addon, chart, kubeconfig, cluster), onLine);
        if (exit != 0)
            throw new InvalidOperationException($"The cluster runs, but {addon.Title} was not installed: {LastLine(error) ?? $"helm exited with {exit}"}");
    }

    static string? LastLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    static async Task<(int Exit, string Error)> Helm(string helm, IEnumerable<string> args, Action<string>? onLine)
    {
        var psi = new ProcessStartInfo(helm) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start helm");
        var error = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) onLine?.Invoke(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (error) error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return (process.ExitCode, error.ToString());
    }
}
