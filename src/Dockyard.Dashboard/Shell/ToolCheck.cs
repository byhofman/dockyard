using System.Diagnostics;
using Dockyard.Kind;

namespace Dockyard.Dashboard.Shell;

// The dashboard talks to the engine itself, but compose projects, terminals and the build history go through the docker
// command and its compose and buildx plugins, which the Toolbox installs as components and can leave out.
sealed class ToolCheck : Observable
{
    bool hasDocker = true;
    bool hasCompose = true;
    bool hasBuildx = true;
    Task? running;

    public bool HasDocker { get => hasDocker; private set { if (Set(ref hasDocker, value)) Raise(nameof(Hint), nameof(HasHint)); } }
    public bool HasCompose { get => hasCompose; private set { if (Set(ref hasCompose, value)) Raise(nameof(Hint), nameof(HasHint)); } }
    public bool HasBuildx { get => hasBuildx; private set { if (Set(ref hasBuildx, value)) Raise(nameof(Hint), nameof(HasHint)); } }

    public string Hint => Missing(HasDocker, HasCompose, HasBuildx) is { Length: > 0 } missing && LastError.Length > 0 ? $"{missing} ({LastError})" : Missing(HasDocker, HasCompose, HasBuildx);
    public bool HasHint => Hint.Length > 0;

    public static string Missing(bool docker, bool compose, bool buildx) =>
        !docker ? "The docker command is not installed, so compose projects, terminals and builds are unavailable. Add the Docker CLI in the Dockyard Toolbox, under Components."
        : !compose && !buildx ? "Docker Compose and Docker Buildx are not installed. Add them in the Dockyard Toolbox, under Components."
        : !compose ? "Docker Compose is not installed, so compose projects cannot be started or changed. Add it in the Dockyard Toolbox, under Components."
        : !buildx ? "Docker Buildx is not installed, so the build history is unavailable. Add it in the Dockyard Toolbox, under Components."
        : "";

    public Task Refresh() => running is { IsCompleted: false } ? running : running = Check();

    async Task Check()
    {
        LastError = "";
        var docker = await Answers("--version");
        HasDocker = docker;
        HasCompose = docker && await Answers("compose", "version");
        HasBuildx = docker && await Answers("buildx", "version");
        Raise(nameof(Hint));
    }

    // What docker said when a plugin check failed, shown with the hint so a plugin it cannot find says why.
    static string LastError = "";

    static async Task<bool> Answers(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(DockerCli.Path()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null) return false;
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0) LastError = $"{DockerCli.Path()} {string.Join(' ', args)}: {(await error).Trim()}";
            return process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException) { return false; }
    }
}
