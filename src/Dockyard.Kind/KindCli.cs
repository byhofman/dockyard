using System.Diagnostics;

namespace Dockyard.Kind;

static class KindCli
{
    static string Exe => OperatingSystem.IsWindows() ? "kind.exe" : "kind";

    // Where Dockyard installs its own copy on each system.
    public static string OwnPath(string home) => System.IO.Path.Combine(CliLocator.KnownDirs(home).First(), Exe);

    public static string? Locate() => CliLocator.Locate("kind");

    public static string? Find(Func<string, bool> exists, string? path, string home) => CliLocator.Find("kind", exists, path, home);

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

    // kind writes to a file of its own; HostKubeconfig puts the entries in the user's kubeconfig safely.
    public static string[] Create(string name, string kubeconfig, string? config = null, NodeImage? image = null) =>
    [
        "create", "cluster", "--name", name, "--kubeconfig", kubeconfig,
        .. config is null ? Array.Empty<string>() : ["--config", config],
        .. image is null ? Array.Empty<string>() : ["--image", image.Reference],
    ];

    // One control plane and this many workers, each mounting the shared folder when asked; without a config kind makes
    // the control plane alone.
    // The shared host folder on or off in a kind config: the same mounts Config gives each node.
    public static string WithSharedStorage(string yaml, bool on)
    {
        var text = yaml.Replace("\r\n", "\n").Replace(SharedStorage.NodeMounts, "");
        return on ? System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^- role: \w+\n", m => m.Value + SharedStorage.NodeMounts) : text;
    }

    public static string Config(int workers, bool sharedStorage = false)
    {
        var mounts = sharedStorage ? SharedStorage.NodeMounts : "";
        return "kind: Cluster\napiVersion: kind.x-k8s.io/v1alpha4\nnodes:\n- role: control-plane\n" + mounts + string.Concat(Enumerable.Repeat("- role: worker\n" + mounts, workers));
    }
    public static string[] Delete(string name, string kubeconfig) => ["delete", "cluster", "--name", name, "--kubeconfig", kubeconfig];
    public static string[] Kubeconfig(string name) => ["get", "kubeconfig", "--name", name];
    public static readonly string[] Clusters = ["get", "clusters"];

    public static async Task<(int ExitCode, string Output, string Error)> Run(string kind, string context, IReadOnlyList<string> args, Action<string>? onLine = null)
    {
        var psi = new ProcessStartInfo(kind)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in Environment(context, DockerCli.Path(), System.Environment.GetEnvironmentVariable("PATH")))
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
