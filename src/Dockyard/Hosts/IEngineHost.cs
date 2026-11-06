namespace Dockyard.Hosts;

sealed record EngineInfo(string Id, string Name, string Title, string Subtitle, string Context, string Endpoint, string? ComponentId = null);

// CheckNeedsEngine: checking this component starts that engine (e.g. the WSL distro), so automatic checks skip it while it is stopped.
// RequiredBecause: it cannot be removed, and the row says why. Flag: its name for --with and --without, and it has Add and Remove.
// Note: what depends on it or what it depends on. IsEngine: the last one left cannot be removed.
sealed record ComponentInfo(string Id, string Title, string? CheckNeedsEngine = null, string? RequiredBecause = null, string? Flag = null, string? Note = null, bool IsEngine = false, string? RequiredFlag = null)
{
    public bool IsOptional => Flag is not null;
}

sealed record Release(Version Version, string Tag, string? DownloadUrl);

// Silent: nothing may pop up, so a step that would need a prompt uses what works without one, or says what was skipped.
sealed record InstallRequest(bool Dashboard, string? Image = null, bool Silent = false)
{
    // Ids from the catalog that stay out, or go if they are there. Empty for almost everyone, and then setup is what it always was.
    public IReadOnlyCollection<string> Without { get; init; } = [];
}

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
    // Adds or removes any component with a flag. Returns true when Dockyard itself is being replaced and should exit.
    // Removing an engine keeps its images unless removeData says otherwise.
    Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false) =>
        component == Core.ComponentRules.DashboardId ? SetDashboard(log, installed) : throw new InvalidOperationException($"{component} cannot be changed here");
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
