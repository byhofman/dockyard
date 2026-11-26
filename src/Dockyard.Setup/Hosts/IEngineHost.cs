namespace Dockyard.Hosts;

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

    // The steps Install runs for this request, in order; it logs InstallPhases.Start as it enters each one.
    IReadOnlyList<string> InstallPlan(InstallRequest request) => ["Installing Dockyard"];

    // Finishes an install that had to wait for a reboot.
    Task Resume(Action<string> log) => Task.CompletedTask;

    // Puts the settings in place, restarting the engine if it runs: it reads them only when it starts.
    Task ApplyEngineConfiguration(Action<string> log) => Task.CompletedTask;

    // Where an installed component came from, as people know it: winget, Homebrew, apt, dnf, or Dockyard for the Dockyard source.
    Task<string?> Source(string component) => Task.FromResult<string?>(null);

    // An engine's daemon.json as the user edits it, without what Dockyard adds; null where it cannot be edited here.
    Task<string?> ReadEngineSettings(string engine) => Task.FromResult<string?>(null);
    // Null when the engine accepts them, otherwise why not.
    Task<string?> ValidateEngineSettings(string engine, string json) => Task.FromResult(Core.EngineSettings.Problem(json));
    // Puts them in place, restarting the engine if it runs. Settings it does not start with are taken back.
    Task ApplyEngineSettings(Action<string> log, string engine, string json) =>
        throw new InvalidOperationException($"The {engine} engine's settings cannot be changed here");

    // Where Linux containers imported from another engine go, started if need be; and the socket Dockyard itself links, if any.
    string ImportTarget => Engines[0].Endpoint;
    string? OwnSocket => null;

    string CaDirectory => Path.Combine(Core.Paths.SettingsDir, "ca");
    Core.ICaKeyStore CaKeys => new Core.FileCaKey(CaDirectory);
    // Returns false when the user or the system declined. Silent: nothing may pop up, so only what works without a prompt.
    Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => Task.FromResult(false);
    Task UntrustCa(Action<string> log, string certificateFile, bool silent) => Task.CompletedTask;

    // What only the tray keeps running, for as long as it runs.
    void StartTrayServices() { }

    void StartApp();
    string DoneMessage(bool reboot);

    // True for the installed copy; false for dockyard setup run from a download.
    bool RunsFromInstallLocation { get; }

    // The component that is Dockyard itself for updates and for the installed version: the dockyard command.
    string SelfComponent => nameof(Core.Component.DockyardCli);
    // Whether updating Dockyard itself stops the tray or the dashboard, as where one bundle or package holds them all.
    bool SelfUpdateStops(string app) => true;

    bool HasDashboard { get; }
    // Where the dashboard is, or would be; null when this host has no fixed place for it.
    string? DashboardExe => null;
    // Returns true when Dockyard itself is being replaced and should exit.
    Task<bool> SetDashboard(Action<string> log, bool installed);
    // Adds or removes any component with a flag. Returns true when Dockyard itself is being replaced and should exit.
    // Removing an engine keeps its images unless removeData says otherwise.
    Task<bool> SetComponent(Action<string> log, string component, bool installed, bool removeData = false) =>
        component == Core.ComponentRules.DashboardId ? SetDashboard(log, installed) : throw new InvalidOperationException($"{component} cannot be changed here");
    Task Uninstall(Action<string> log, bool removeData, bool silent);

    void ReleaseResources();
}
