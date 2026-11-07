using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.Hosts;

namespace Dockyard.ViewModels;

sealed class MainViewModel : Observable
{
    static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    readonly Settings settings;
    readonly IEngineHost host;
    readonly List<Command> commands = [];
    readonly StringBuilder log = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    DateTime lastCheck = DateTime.MinValue;
    string? busy;
    string status = "Ready";
    bool isSetupMode;
    bool isInstalled;
    bool removeData;
    bool needsReboot;
    string installedVersion = "–";
    readonly SetupOptions options;
    string active;

    public MainViewModel(IEngineHost host, bool setupMode, SetupOptions? options = null, Settings? userSettings = null)
    {
        this.host = host;
        settings = userSettings ?? Settings.Load();
        this.options = options ?? new SetupOptions();
        isSetupMode = setupMode;
        active = host.Engines[0].Id;

        EngineCards = host.Engines.Select(e => new EngineViewModel(e)
        {
            ToggleCommand = Cmd(() => Toggle(e.Id)),
            UseCommand = Cmd(() => Switch(e.Id)),
        }).ToList();

        var catalog = host.Components.Any(c => c.Id == ComponentRules.DashboardId)
            ? host.Components
            : [.. host.Components, new ComponentInfo(ComponentRules.DashboardId, "Dashboard", Flag: "dashboard")];
        foreach (var c in catalog)
        {
            ComponentViewModel? item = null;
            item = new ComponentViewModel(c)
            {
                UpdateCommand = Cmd(() => UpdateComponents([item!]), () => item!.IsAvailable),
                AddCommand = Cmd(() => SetComponent(item!, true)),
                RemoveCommand = Cmd(() => SetComponent(item!, false)),
            };
            Components.Add(item);
        }
        Dashboard = Components.First(c => c.Id == ComponentRules.DashboardId);
        // What the command line already chose is what the first install's boxes start from.
        Dashboard.Include = this.options.Dashboard ?? true;
        foreach (var c in Components.Where(c => c.Info.Flag is { } flag && this.options.Without.Contains(flag))) c.Include = false;
        // Until a check says otherwise, which automatic checks skip for an engine that is stopped.
        foreach (var c in Components) c.IsPresent = !settings.RemovedComponents.Contains(c.Id);
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

    public bool IncludeDashboard { get => Dashboard.Include; set { Dashboard.Include = value; Raise(nameof(IncludeDashboard)); } }
    public IEnumerable<ComponentViewModel> OptionalChoices => Components.Where(c => c.IsOptional);
    public string RequiredSummary => string.Join("\n", Components.Where(c => c.Info.RequiredBecause is not null).Select(c => $"{c.Title}: {c.Info.RequiredBecause}"));
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

    // Its version comes from the update check; whether it is there at all is known without one, as soon as the window opens.
    void RefreshDashboard()
    {
        Dashboard.IsPresent = host.HasDashboard;
        if (Dashboard.State is UpdateState.Unknown or UpdateState.OptionalInstalled or UpdateState.OptionalAbsent || !Dashboard.IsPresent)
            Dashboard.State = host.HasDashboard ? UpdateState.OptionalInstalled : UpdateState.OptionalAbsent;
    }

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
        foreach (var e in EngineCards) e.IsAvailable = e.Info.ComponentId is not { } id || !settings.RemovedComponents.Contains(id);
        Raise(nameof(Active), nameof(ActiveEngine));
        StateChanged?.Invoke();
    }

    async Task CheckUpdates(bool all)
    {
        var targets = Components
            .Where(c => all || c.Info.CheckNeedsEngine is not { } needs || EngineCards.Any(e => e.Id == needs && e.IsRunning))
            .ToList();
        foreach (var c in targets) c.State = UpdateState.Checking;
        await Task.WhenAll(targets.Select(Check));
        lastCheck = DateTime.UtcNow;
        UpdateAllCommand.Refresh();

        var available = Components.Count(c => c.IsAvailable);
        var allChecked = !targets.Any(c => c.State is UpdateState.Error or UpdateState.CouldNotCheck);
        Log(available > 0 ? $"{available} update(s) available"
            : allChecked ? "Everything is up to date"
            : "Not everything could be checked; try again later");
    }

    async Task Check(ComponentViewModel c)
    {
        try
        {
            var installed = await host.Installed(c.Id);
            c.Installed = installed?.ToString() ?? "–";
            if (c.Id == nameof(Component.App)) InstalledVersion = c.Installed;
            c.IsPresent = c == Dashboard ? host.HasDashboard : installed is not null;
            var latest = await host.Latest(c.Id);
            c.LatestRelease = latest;
            c.Latest = latest?.Version.ToString() ?? "–";
            c.Error = null;
            c.State = !c.IsPresent && c.IsOptional ? UpdateState.OptionalAbsent
                : installed is null ? c.IsOptional ? UpdateState.OptionalInstalled : UpdateState.NotInstalled
                : latest is null ? UpdateState.NoRelease
                : latest.Version > installed ? UpdateState.Available
                : UpdateState.UpToDate;
        }
        catch (HttpRequestException ex) when (Updates.IsRateLimited(ex))
        {
            c.Error = ex.Message;
            c.State = UpdateState.CouldNotCheck;
            Log($"{c.Title}: {ex.Message}");
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
        if (!IsInstalled)
        {
            var left = Components.Where(c => c.IsOptional && !c.Include && c != Dashboard).Select(c => c.Id).Order().ToList();
            if (ComponentRules.Problems(host.Components, [], [], left) is { Count: > 0 } problems) throw new InvalidOperationException(problems[0]);
            if (!left.SequenceEqual(settings.RemovedComponents))
            {
                settings.RemovedComponents = left;
                settings.Save();
            }
        }
        var reboot = await host.Install(Log, new InstallRequest(dashboard) { Without = settings.RemovedComponents });
        IsInstalled = true;
        NeedsReboot = reboot;
        RefreshDashboard();
        if (!reboot && options.Start) host.StartApp();
        Log(host.DoneMessage(reboot));
        Status = reboot ? "Restart to finish" : "Installed";
        foreach (var c in Components) await Check(c);
    }

    // Rules first: what is required, or the last engine, is refused with the reason and nothing changes.
    async Task SetComponent(ComponentViewModel item, bool installed)
    {
        if (!installed)
        {
            var present = Components.Where(c => c.IsPresent).Select(c => c.Id).ToHashSet();
            if (ComponentRules.WhyNotRemovable(Components.Select(c => c.Info).ToList(), item.Id, present) is { } why)
            {
                Log(why);
                Status = why;
                return;
            }
        }
        await Run($"{(installed ? "Adding" : "Removing")} {item.Title}", async () =>
        {
            if (await host.SetComponent(Log, item.Id, installed))
            {
                ShutdownRequested?.Invoke();
                return;
            }
            if (item != Dashboard)
            {
                if (installed) settings.RemovedComponents.Remove(item.Id);
                else if (!settings.RemovedComponents.Contains(item.Id)) settings.RemovedComponents.Add(item.Id);
                settings.Save();
            }
            await Check(item);
            RefreshDashboard();
            Raise(nameof(HasDashboard));
        });
    }

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
