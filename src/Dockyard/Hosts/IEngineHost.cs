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

    // Finishes an install that had to wait for a reboot.
    Task Resume(Action<string> log) => Task.CompletedTask;

    // Registry mirrors, insecure registries and address pools on an engine Dockyard owns; a Linux host's file is the user's.
    bool HasEngineConfiguration => false;
    // Puts the settings in place, restarting the engine if it runs: it reads them only when it starts.
    Task ApplyEngineConfiguration(Action<string> log) => Task.CompletedTask;

    // Where Linux containers imported from another engine go, started if need be; and the socket Dockyard itself links, if any.
    string ImportTarget => Engines[0].Endpoint;
    string? OwnSocket => null;

    string CaDirectory => Path.Combine(Core.Paths.SettingsDir, "ca");
    Core.ICaKeyStore CaKeys => new Core.FileCaKey(CaDirectory);
    // Returns false when the user or the system declined. Silent: nothing may pop up, so only what works without a prompt.
    Task<bool> TrustCa(Action<string> log, string certificateFile, bool silent) => Task.FromResult(false);
    Task UntrustCa(Action<string> log, string certificateFile) => Task.CompletedTask;

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
