using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.ViewModels;

sealed class MainViewModel : Observable
{
    static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    readonly Settings settings = Settings.Load();
    readonly IEngineHost host;
    readonly List<Command> commands = [];
    readonly StringBuilder log = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    DateTime lastCheck = DateTime.MinValue;
    string? busy;
    string status = "Ready";
    bool isSetupMode;
    bool isInstalled;
    bool includeDashboard;
    bool removeData;
    bool needsReboot;
    string installedVersion = "–";
    readonly SetupOptions options;
    string active;

    public MainViewModel(IEngineHost host, bool setupMode, SetupOptions? options = null)
    {
        this.host = host;
        this.options = options ?? new SetupOptions();
        includeDashboard = this.options.Dashboard ?? true;
        isSetupMode = setupMode;
        active = host.Engines[0].Id;

        EngineCards = host.Engines.Select(e => new EngineViewModel(e)
        {
            ToggleCommand = Cmd(() => Toggle(e.Id)),
            UseCommand = Cmd(() => Switch(e.Id)),
        }).ToList();

        foreach (var c in host.Components)
        {
            ComponentViewModel? item = null;
            item = new ComponentViewModel(c) { UpdateCommand = Cmd(() => UpdateComponents([item!]), () => item!.IsAvailable) };
            Components.Add(item);
        }

        Dashboard = new ComponentViewModel(new ComponentInfo(OptionalComponents.Dashboard, "Dashboard"))
        {
            UpdateCommand = Cmd(() => Task.CompletedTask, () => false),
            AddCommand = Cmd(() => SetDashboard(true)),
            RemoveCommand = Cmd(() => SetDashboard(false)),
        };
        Components.Add(Dashboard);
        RefreshDashboard();

        CheckUpdatesCommand = Cmd(() => Run("Checking for updates", () => CheckUpdates(all: true)));
        UpdateAllCommand = Cmd(() => UpdateComponents(Components.Where(c => c.IsAvailable).ToList()), () => Components.Any(c => c.IsAvailable));
        InstallCommand = Cmd(Install);
        UninstallCommand = Cmd(Uninstall);
        RebootCommand = Cmd(() => { host.Reboot(); return Task.CompletedTask; });
        ApplyEngineConfigurationCommand = Cmd(ApplyEngineConfiguration);
        ImportCommand = Cmd(() => Run("Importing from Docker Desktop", async () =>
        {
            await host.Start(host.Engines[0].Id);
            await DockerDesktop.Import(null, host.ImportTarget, host.OwnSocket, Log);
        }));
        EnableHttpsCommand = Cmd(() => Run("Turning on HTTPS for container domains", async () =>
        {
            await Https.Enable(host, Log, silent: false);
            settings.HttpsEnabled = true;
            Raise(nameof(HttpsEnabled), nameof(HttpsDisabled));
        }));
        DisableHttpsCommand = Cmd(() => Run("Turning off HTTPS for container domains", async () =>
        {
            await Https.Disable(host, Log);
            settings.HttpsEnabled = false;
            Raise(nameof(HttpsEnabled), nameof(HttpsDisabled));
        }));
        registryMirrors = settings.RegistryMirrors ?? "";
        insecureRegistries = settings.InsecureRegistries ?? "";
        addressPools = settings.AddressPools ?? "";

        timer.Tick += async (_, _) => await RefreshState();
    }

    readonly CancellationTokenSource trayLifetime = new();

    public void StartPolling() => timer.Start();

    public event Action? StateChanged;
    public event Action? ShutdownRequested;

    public IReadOnlyList<EngineViewModel> EngineCards { get; }
    public ObservableCollection<ComponentViewModel> Components { get; } = [];

    public Command CheckUpdatesCommand { get; }
    public Command UpdateAllCommand { get; }
    public Command InstallCommand { get; }
    public Command UninstallCommand { get; }
    public Command RebootCommand { get; }
    public Command ApplyEngineConfigurationCommand { get; }
    public Command ImportCommand { get; }
    public Command EnableHttpsCommand { get; }
    public Command DisableHttpsCommand { get; }
    public bool HttpsEnabled => settings.HttpsEnabled;
    public bool HttpsDisabled => !settings.HttpsEnabled;
    public bool ShowImport => ShowManage && DockerDesktop.IsInstalled;
    public ComponentViewModel Dashboard { get; }

    string registryMirrors;
    string insecureRegistries;
    string addressPools;
    string engineConfigurationProblem = "";
    public string RegistryMirrors { get => registryMirrors; set => Set(ref registryMirrors, value); }
    public string InsecureRegistries { get => insecureRegistries; set => Set(ref insecureRegistries, value); }
    public string AddressPools { get => addressPools; set => Set(ref addressPools, value); }
    public string EngineConfigurationProblem { get => engineConfigurationProblem; private set { if (Set(ref engineConfigurationProblem, value)) Raise(nameof(HasEngineConfigurationProblem)); } }
    public bool HasEngineConfigurationProblem => engineConfigurationProblem.Length > 0;
    public bool ShowEngineConfiguration => ShowManage && host.HasEngineConfiguration;

    public bool IncludeDashboard { get => includeDashboard; set => Set(ref includeDashboard, value); }
    public bool RemoveData { get => removeData; set => Set(ref removeData, value); }
    public bool HasDashboard => host.HasDashboard;
    public bool NeedsReboot { get => needsReboot; private set => Set(ref needsReboot, value); }
    public string InstalledVersion { get => installedVersion; private set { if (Set(ref installedVersion, value)) Raise(nameof(ManageText)); } }
    public string ManageText => InstalledVersion == Paths.AppVersion
        ? $"Dockyard {InstalledVersion} is installed. Running the setup again keeps your images, containers and settings."
        : $"Dockyard {InstalledVersion} is installed. Update brings it to {Paths.AppVersion} and keeps your images, containers and settings.";

    public string Version => "v" + Paths.AppVersion;
    public string Active => active;
    public EngineViewModel ActiveEngine => EngineCards.FirstOrDefault(e => e.Id == active) ?? EngineCards[0];
    public bool HasEngineSwitch => IsDashboard && EngineCards.Count > 1;

    public string SetupDescription => OperatingSystem.IsWindows()
        ? "Installs the Windows Docker engine (dockerd service), the docker CLI with compose and buildx, a dedicated 'dockyard' WSL distro for Linux containers, and Dockyard itself, which runs in the tray."
        : OperatingSystem.IsMacOS()
            ? "Installs the docker CLI with compose and buildx, a small Linux VM for containers that starts when you need it, and Dockyard itself, which runs in the menu bar."
            : "Installs Docker CE with compose and buildx from Docker's repository, starts it on demand, and Dockyard itself, which runs in the tray.";

    public bool IsSetupMode
    {
        get => isSetupMode;
        private set { if (Set(ref isSetupMode, value)) Raise(nameof(IsDashboard), nameof(HasEngineSwitch), nameof(ShowInstall), nameof(ShowManage), nameof(ShowWizardManage), nameof(ShowEngineConfiguration), nameof(ShowImport)); }
    }

    public bool IsDashboard => !IsSetupMode;
    public bool IsInstalled
    {
        get => isInstalled;
        private set { if (Set(ref isInstalled, value)) Raise(nameof(ShowInstall), nameof(ShowManage), nameof(ShowWizardManage), nameof(ShowEngineConfiguration), nameof(ShowImport)); }
    }

    // The wizard installs once; on an existing install it is the same manage view as the tray window.
    public bool ShowInstall => IsSetupMode && !IsInstalled;
    public bool ShowManage => !IsSetupMode || IsInstalled;
    public bool ShowWizardManage => IsSetupMode && IsInstalled;

    public string? Busy
    {
        get => busy;
        private set
        {
            if (!Set(ref busy, value)) return;
            Raise(nameof(IsBusy));
            foreach (var c in commands) c.Refresh();
            StateChanged?.Invoke();
        }
    }

    public bool IsBusy => busy is not null;
    public string Status { get => status; private set => Set(ref status, value); }
    public string LogText => log.ToString();

    public bool StopOtherOnSwitch
    {
        get => settings.StopOtherOnSwitch;
        set { settings.StopOtherOnSwitch = value; settings.Save(); Raise(nameof(StopOtherOnSwitch)); }
    }

    public bool StartActiveAtLogon
    {
        get => settings.StartActiveAtLogon;
        set { settings.StartActiveAtLogon = value; settings.Save(); Raise(nameof(StartActiveAtLogon)); }
    }

    public async Task StartTray()
    {
        host.StartTrayServices();
        _ = Https.KeepCertificateCurrent(host, trayLifetime.Token);
        await RefreshState();
        if (settings.StartActiveAtLogon)
            await Run($"Starting {ActiveEngine.Name} engine", () => host.Start(active));
    }

    public async Task LoadSetup()
    {
        IsInstalled = host.IsInstalled;
        await RefreshState();
        if (options.Resume)
        {
            await Run("Finishing the setup", async () =>
            {
                await host.Resume(Log);
                host.StartApp();
                Log(host.DoneMessage(reboot: false));
                IsInstalled = true;
            });
        }
        if (IsInstalled) await Run("Checking for updates", () => CheckUpdates(all: false));
    }

    void RefreshDashboard() => Dashboard.State = host.HasDashboard ? UpdateState.OptionalInstalled : UpdateState.OptionalAbsent;

    public async Task OnWindowShown()
    {
        await RefreshState();
        if (!IsSetupMode && !IsBusy && DateTime.UtcNow - lastCheck > AutoCheckInterval)
            await Run("Checking for updates", () => CheckUpdates(all: false));
    }

    public Task Switch(string engine) => Run($"Switching to {Name(engine)} containers", async () =>
    {
        await host.Start(engine);
        await host.UseContext(engine);
        if (settings.StopOtherOnSwitch)
            foreach (var other in EngineCards.Where(e => e.Id != engine)) await host.Stop(other.Id);
    });

    public Task StartEngine(string engine) => Run($"Starting {Name(engine)} engine", () => host.Start(engine));

    public void Shutdown()
    {
        timer.Stop();
        trayLifetime.Cancel();
        host.ReleaseResources();
    }

    string Name(string engine) => EngineCards.FirstOrDefault(e => e.Id == engine)?.Name ?? engine;

    async Task Toggle(string engine)
    {
        if (EngineCards.First(e => e.Id == engine).IsRunning)
            await Run($"Stopping {Name(engine)} engine", () => host.Stop(engine));
        else
            await Run($"Starting {Name(engine)} engine", () => host.Start(engine));
    }

    async Task RefreshState()
    {
        foreach (var e in EngineCards) e.IsRunning = await host.IsRunning(e.Id);
        active = host.ActiveEngine();
        foreach (var e in EngineCards) e.IsActive = e.Id == active;
        RefreshDashboard();
        Raise(nameof(Active), nameof(ActiveEngine));
        StateChanged?.Invoke();
    }

    async Task CheckUpdates(bool all)
    {
        var targets = Components
            .Where(c => c != Dashboard)
            .Where(c => all || c.Info.CheckNeedsEngine is not { } needs || EngineCards.Any(e => e.Id == needs && e.IsRunning))
            .ToList();
        foreach (var c in targets) c.State = UpdateState.Checking;
        await Task.WhenAll(targets.Select(Check));
        lastCheck = DateTime.UtcNow;
        UpdateAllCommand.Refresh();

        var available = Components.Count(c => c.IsAvailable);
        Log(available == 0 ? "Everything is up to date" : $"{available} update(s) available");
    }

    async Task Check(ComponentViewModel c)
    {
        try
        {
            var installed = await host.Installed(c.Id);
            c.Installed = installed?.ToString() ?? "–";
            if (c.Id == nameof(Component.App)) InstalledVersion = c.Installed;
            var latest = await host.Latest(c.Id);
            c.LatestRelease = latest;
            c.Latest = latest?.Version.ToString() ?? "–";
            c.Error = null;
            c.State = installed is null ? UpdateState.NotInstalled
                : latest is null ? UpdateState.NoRelease
                : latest.Version > installed ? UpdateState.Available
                : UpdateState.UpToDate;
        }
        catch (Exception ex)
        {
            c.Error = ex.Message;
            c.State = UpdateState.Error;
            Log($"{c.Title}: {ex.Message}");
        }
        finally
        {
            c.UpdateCommand.Refresh();
        }
    }

    Task UpdateComponents(IReadOnlyList<ComponentViewModel> items) => Run("Updating", async () =>
    {
        // The wizard is the new version: installing it also does what the jump from an old version needs, such as
        // moving to the dockyard distro, which replacing only the app would skip.
        if (IsSetupMode && items.Any(i => i.Id == nameof(Component.App)))
        {
            await InstallCore();
            items = items.Where(i => i.Id != nameof(Component.App)).ToList();
        }
        if (await host.Update(Log, items.Select(i => (i.Id, i.LatestRelease)).ToList()))
        {
            ShutdownRequested?.Invoke();
            return;
        }
        foreach (var item in items) await Check(item);
    });

    Task ApplyEngineConfiguration()
    {
        var problems = EngineOptions.Problems(RegistryMirrors, InsecureRegistries, AddressPools);
        EngineConfigurationProblem = string.Join('\n', problems);
        if (problems.Count > 0) return Task.CompletedTask;
        settings.RegistryMirrors = RegistryMirrors;
        settings.InsecureRegistries = InsecureRegistries;
        settings.AddressPools = AddressPools;
        settings.Save();
        return Run("Applying the engine configuration", () => host.ApplyEngineConfiguration(Log));
    }

    Task Install() => Run("Installing", InstallCore);

    async Task InstallCore()
    {
        var dashboard = IsInstalled ? DashboardChoice.Resolve(options.Dashboard, true, host.HasDashboard, host.RunsFromInstallLocation) : IncludeDashboard;
        var reboot = await host.Install(Log, new InstallRequest(dashboard));
        IsInstalled = true;
        NeedsReboot = reboot;
        RefreshDashboard();
        if (!reboot && options.Start) host.StartApp();
        Log(host.DoneMessage(reboot));
        Status = reboot ? "Restart to finish" : "Installed";
        foreach (var c in Components.Where(c => c != Dashboard)) await Check(c);
    }

    Task SetDashboard(bool installed) => Run(installed ? "Adding the dashboard" : "Removing the dashboard", async () =>
    {
        if (await host.SetDashboard(Log, installed))
        {
            ShutdownRequested?.Invoke();
            return;
        }
        RefreshDashboard();
        Raise(nameof(HasDashboard));
    });

    Task Uninstall() => Run("Uninstalling", async () =>
    {
        await host.Uninstall(Log, RemoveData);
        IsInstalled = false;
        Log(RemoveData ? "Dockyard was removed, with its images and containers." : "Dockyard was removed. Images and containers were kept.");
        if (!IsSetupMode) ShutdownRequested?.Invoke();
    });

    async Task Run(string what, Func<Task> action)
    {
        if (Busy is not null) return;
        Busy = what;
        Status = what + "…";
        Log("› " + what);
        try
        {
            await action();
            Status = "Ready";
        }
        catch (Exception ex)
        {
            Log("✖ " + ex.Message);
            Status = ex.Message;
        }
        finally
        {
            Busy = null;
            if (!IsSetupMode) await RefreshState();
        }
    }

    public void Note(string line) => Log(line);

    void Log(string line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Log(line));
            return;
        }
        log.AppendLine(line);
        Raise(nameof(LogText));
    }

    Command Cmd(Func<Task> run, Func<bool>? canRun = null)
    {
        var command = new Command(run, () => Busy is null && (canRun?.Invoke() ?? true));
        commands.Add(command);
        return command;
    }
}
