namespace Dockyard.Hosts;

sealed record EngineInfo(string Id, string Name, string Title, string Subtitle, string Context, string Endpoint);

// CheckNeedsEngine: checking this component starts that engine (e.g. the WSL distro), so automatic checks skip it while it is stopped.
sealed record ComponentInfo(string Id, string Title, string? CheckNeedsEngine = null);

sealed record Release(Version Version, string Tag, string? DownloadUrl);

// Silent: nothing may pop up, so a step that would need a prompt uses what works without one, or says what was skipped.
sealed record InstallRequest(bool Dashboard, string? Image = null, bool Silent = false);

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

    // Installs, or brings an existing install up to date and in line with the request, keeping images and containers.
    // Returns true when a reboot is needed to finish.
    Task<bool> Install(Action<string> log, InstallRequest request);

    // Finishes an install that had to wait for a reboot.
    Task Resume(Action<string> log) => Task.CompletedTask;

    // Registry mirrors, insecure registries and address pools on an engine Dockyard owns; a Linux host's file is the user's.
    bool HasEngineConfiguration => false;
    // Puts the settings in place, restarting the engine if it runs: it reads them only when it starts.
    Task ApplyEngineConfiguration(Action<string> log) => Task.CompletedTask;

    // Where Linux containers imported from another engine go, started if need be; and the socket Dockyard itself links, if any.
    string ImportTarget => Engines[0].Endpoint;
    string? OwnSocket => null;

    // What only the tray keeps running, for as long as it runs.
    void StartTrayServices() { }

    void StartApp();
    string DoneMessage(bool reboot);
    void Reboot() { }

    // True for the tray and for a copy an earlier script placed; false for the wizard run from a download.
    bool RunsFromInstallLocation { get; }

    bool HasDashboard { get; }
    // Returns true when Dockyard itself is being replaced and should exit.
    Task<bool> SetDashboard(Action<string> log, bool installed);
    Task Uninstall(Action<string> log, bool removeData);

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

static class Wait
{
    public static async Task For(Func<bool> ready, TimeSpan timeout, string what, Func<bool>? failed = null)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await Task.Run(ready))
        {
            if (failed?.Invoke() == true) throw new InvalidOperationException($"{what} failed to start");
            if (DateTime.UtcNow > until) throw new TimeoutException($"{what} not ready after {timeout.TotalSeconds:0}s");
            await Task.Delay(500);
        }
    }
}
