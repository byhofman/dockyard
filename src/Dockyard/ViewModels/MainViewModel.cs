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
    bool engineOnly;
    string active;

    public MainViewModel(IEngineHost host, bool setupMode)
    {
        this.host = host;
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

        CheckUpdatesCommand = Cmd(() => Run("Checking for updates", () => CheckUpdates(all: true)));
        UpdateAllCommand = Cmd(() => UpdateComponents(Components.Where(c => c.IsAvailable).ToList()), () => Components.Any(c => c.IsAvailable));
        InstallCommand = Cmd(Install);
        AddDashboardCommand = Cmd(() => SetDashboard(true));
        RemoveDashboardCommand = Cmd(() => SetDashboard(false));
        UninstallCommand = Cmd(Uninstall);

        timer.Tick += async (_, _) => await RefreshState();
        timer.Start();
    }

    public event Action? StateChanged;
    public event Action? ShutdownRequested;

    public IReadOnlyList<EngineViewModel> EngineCards { get; }
    public ObservableCollection<ComponentViewModel> Components { get; } = [];

    public Command CheckUpdatesCommand { get; }
    public Command UpdateAllCommand { get; }
    public Command InstallCommand { get; }
    public Command UninstallCommand { get; }
    public Command AddDashboardCommand { get; }
    public Command RemoveDashboardCommand { get; }

    public bool EngineOnly { get => engineOnly; set => Set(ref engineOnly, value); }
    public bool HasDashboard => host.HasDashboard;
    public string DashboardText => HasDashboard ? "Dashboard installed" : "Dashboard not installed (engine-only)";

    public string Version => "v" + Paths.AppVersion;
    public string Active => active;
    public EngineViewModel ActiveEngine => EngineCards.FirstOrDefault(e => e.Id == active) ?? EngineCards[0];
    public bool HasEngineSwitch => IsDashboard && EngineCards.Count > 1;

    public string SetupDescription => OperatingSystem.IsWindows()
        ? "Installs the Windows Docker engine (dockerd service), the docker CLI with compose and buildx, a dedicated 'dockyard' WSL distro for Linux containers, and Dockyard itself, which runs in the tray."
        : "Installs Docker CE with compose and buildx from Docker's repository, starts it on demand, and Dockyard itself, which runs in the tray.";

    public bool IsSetupMode
    {
        get => isSetupMode;
        private set { if (Set(ref isSetupMode, value)) Raise(nameof(IsDashboard), nameof(HasEngineSwitch)); }
    }

    public bool IsDashboard => !IsSetupMode;
    public bool IsInstalled { get => isInstalled; private set => Set(ref isInstalled, value); }

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
        await RefreshState();
        if (settings.StartActiveAtLogon)
            await Run($"Starting {ActiveEngine.Name} engine", () => host.Start(active));
    }

    public Task LoadSetup()
    {
        IsInstalled = host.IsInstalled;
        return Task.CompletedTask;
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
        Log(available == 0 ? "Everything is up to date" : $"{available} update(s) available");
    }

    async Task Check(ComponentViewModel c)
    {
        try
        {
            var installed = await host.Installed(c.Id);
            c.Installed = installed?.ToString() ?? "–";
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
        if (await host.Update(Log, items.Select(i => (i.Id, i.LatestRelease)).ToList()))
        {
            ShutdownRequested?.Invoke();
            return;
        }
        foreach (var item in items) await Check(item);
    });

    Task Install() => Run("Installing", async () =>
    {
        var reboot = await host.Install(Log, EngineOnly);
        IsInstalled = true;
        if (OperatingSystem.IsWindows() && !Paths.IsInstalledCopy) Shell.StartUnelevated(Paths.AppExe);
        Log(reboot
            ? "Done. Reboot to finish enabling Windows containers."
            : OperatingSystem.IsWindows()
                ? "Done. Dockyard runs in the tray. Sign out and back in once so docker-users applies to the Windows engine."
                : "Done. Dockyard runs in the tray. Sign out and back in once so the docker group applies.");
        Status = "Installed";
    });

    Task SetDashboard(bool installed) => Run(installed ? "Adding the dashboard" : "Removing the dashboard", async () =>
    {
        if (await host.SetDashboard(Log, installed))
        {
            ShutdownRequested?.Invoke();
            return;
        }
        Raise(nameof(HasDashboard), nameof(DashboardText));
    });

    Task Uninstall() => Run("Uninstalling", async () =>
    {
        await host.Uninstall(Log);
        IsInstalled = false;
        Log("Dockyard was removed. Images and containers were kept.");
        if (Paths.IsInstalledCopy) ShutdownRequested?.Invoke();
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
