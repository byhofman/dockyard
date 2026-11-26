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

    public static readonly IReadOnlyList<ClusterAddon> All = [MetricsServer, Traefik, CertManager.Addon, Headlamp.Addon];

    // kind's own kubeconfig, never the user's: its context for the cluster is kind-<name>. Helm gets repositories of its
    // own in scratch: with --repo it still reads the user's list, and fails on a repository there whose cache is gone.
    public static string[] InstallArgs(ClusterAddon addon, string kubeconfig, string cluster, string scratch) =>
    [
        "upgrade", "--install", addon.Release, addon.Chart, "--repo", addon.Repo, "--version", addon.Version,
        "--namespace", addon.Namespace, "--create-namespace", "--kubeconfig", kubeconfig, "--kube-context", KindKubeconfig.Key(cluster),
        "--repository-config", Path.Combine(scratch, "repositories.yaml"), "--repository-cache", Path.Combine(scratch, "cache"),
        .. addon.Values.SelectMany(v => new[] { "--set", v }),
    ];

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

    public static async Task Install(string helm, ClusterAddon addon, string kubeconfig, string cluster, string scratch, Action<string>? onLine)
    {
        onLine?.Invoke($"Installing {addon.Title} {addon.Version} with Helm");
        var psi = new ProcessStartInfo(helm) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in InstallArgs(addon, kubeconfig, cluster, scratch)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start helm");
        var error = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 } line) onLine?.Invoke(line); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The cluster runs, but {addon.Title} was not installed: "
                + (error.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"helm exited with {process.ExitCode}"));
    }
}
