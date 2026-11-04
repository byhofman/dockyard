namespace Dockyard.Hosts;

sealed record EngineInfo(string Id, string Name, string Title, string Subtitle, string Context, string Endpoint);

// CheckNeedsEngine: checking this component starts that engine (e.g. the WSL distro), so automatic checks skip it while it is stopped.
sealed record ComponentInfo(string Id, string Title, string? CheckNeedsEngine = null);

sealed record Release(Version Version, string Tag, string? DownloadUrl);

interface IEngineHost
{
    IReadOnlyList<EngineInfo> Engines { get; }
    IReadOnlyList<ComponentInfo> Components { get; }
    bool IsInstalled { get; }

    Task<bool> IsRunning(string engine);
    Task Start(string engine);
    Task Stop(string engine);
    string ActiveEngine();
    Task UseContext(string engine);

    Task<Version?> Installed(string component);
    Task<Release?> Latest(string component);

    // Returns true when Dockyard itself is being replaced and should exit.
    Task<bool> Update(Action<string> log, IReadOnlyList<(string Component, Release? Release)> components);

    // Returns true when a reboot is needed to finish.
    Task<bool> Install(Action<string> log);
    Task Uninstall(Action<string> log);

    void ReleaseResources();
}

interface ICommandRunner
{
    Task<Core.ShellResult> Run(string file, params string[] args);
    System.Diagnostics.Process Spawn(string file, params string[] args);
}

sealed class ProcessRunner : ICommandRunner
{
    public static readonly ProcessRunner Instance = new();
    public Task<Core.ShellResult> Run(string file, params string[] args) => Core.Shell.Run(file, args);
    public System.Diagnostics.Process Spawn(string file, params string[] args) => Core.Shell.Spawn(file, args);
}

static class RunnerExtensions
{
    public static async Task<string> Check(this ICommandRunner runner, string file, params string[] args)
    {
        var r = await runner.Run(file, args);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} {string.Join(' ', args)} failed ({r.ExitCode}): {(r.Error + r.Output).Trim()}");
        return r.Output;
    }
}
