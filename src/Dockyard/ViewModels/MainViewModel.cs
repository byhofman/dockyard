using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Dockyard.Core;

namespace Dockyard.ViewModels;

sealed class MainViewModel : Observable
{
    static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    readonly Settings settings = Settings.Load();
    readonly Engines engines;
    readonly List<Command> commands = [];
    readonly StringBuilder log = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    DateTime lastCheck = DateTime.MinValue;
    string? busy;
    string status = "Ready";
    string? selectedDistro;
    bool isSetupMode;
    bool isInstalled;
    Engine active;

    public MainViewModel(bool setupMode)
    {
        engines = new Engines(settings);
        isSetupMode = setupMode;

        Linux = new EngineViewModel(Engine.Linux, "Linux containers", settings.Distro, Paths.LinuxHost)
        {
            ToggleCommand = Cmd(() => Toggle(Engine.Linux)),
            UseCommand = Cmd(() => Switch(Engine.Linux)),
        };
        Windows = new EngineViewModel(Engine.Windows, "Windows containers", "ltsc2025 · process isolation", Paths.WindowsHost)
        {
            ToggleCommand = Cmd(() => Toggle(Engine.Windows)),
            UseCommand = Cmd(() => Switch(Engine.Windows)),
        };
        EngineCards = [Linux, Windows];

        foreach (var c in Enum.GetValues<Component>())
        {
            ComponentViewModel? item = null;
            item = new ComponentViewModel(c) { UpdateCommand = Cmd(() => UpdateComponents([item!]), () => item!.IsAvailable) };
            Components.Add(item);
        }

        CheckUpdatesCommand = Cmd(() => Run("Checking for updates", () => CheckUpdates(includeWsl: true)));
        UpdateAllCommand = Cmd(() => UpdateComponents(Components.Where(c => c.IsAvailable).ToList()), () => Components.Any(c => c.IsAvailable));
        InstallCommand = Cmd(Install, () => SelectedDistro is not null);
        UninstallCommand = Cmd(Uninstall);

        timer.Tick += async (_, _) => await RefreshState();
        timer.Start();
    }

    public event Action? StateChanged;
    public event Action? ShutdownRequested;

    public EngineViewModel Linux { get; }
    public EngineViewModel Windows { get; }
    public IReadOnlyList<EngineViewModel> EngineCards { get; }
    public ObservableCollection<ComponentViewModel> Components { get; } = [];
    public ObservableCollection<string> Distros { get; } = [];

    public Command CheckUpdatesCommand { get; }
    public Command UpdateAllCommand { get; }
    public Command InstallCommand { get; }
    public Command UninstallCommand { get; }

    public string Version => "v" + Paths.AppVersion;
    public Engine Active => active;
    public bool IsLinuxActive => active == Engine.Linux;
    public bool IsWindowsActive => active == Engine.Windows;

    public bool IsSetupMode { get => isSetupMode; private set { if (Set(ref isSetupMode, value)) Raise(nameof(IsDashboard)); } }
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

    public string? SelectedDistro
    {
        get => selectedDistro;
        set { if (Set(ref selectedDistro, value)) InstallCommand.Refresh(); }
    }

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
            await Run($"Starting {Engines.Name(active)} engine", () => engines.Start(active));
    }

    public async Task LoadSetup()
    {
        var distros = await UserSetup.ListDistros();
        Distros.Clear();
        foreach (var d in distros) Distros.Add(d);
        SelectedDistro = distros.Contains(settings.Distro) ? settings.Distro : distros.FirstOrDefault();
        IsInstalled = File.Exists(Paths.AppExe);
        if (distros.Length == 0)
            Log("No WSL distro found. Install one (winget install Canonical.Ubuntu.2404), launch it once, then reopen Dockyard.");
    }

    public async Task OnWindowShown()
    {
        await RefreshState();
        if (!IsSetupMode && !IsBusy && DateTime.UtcNow - lastCheck > AutoCheckInterval)
            await Run("Checking for updates", () => CheckUpdates(includeWsl: Linux.IsRunning));
    }

    public Task Switch(Engine target) => Run($"Switching to {Engines.Name(target)} containers", () => engines.Switch(target));

    public void Shutdown()
    {
        timer.Stop();
        engines.ReleaseKeepAlive();
    }

    Task Toggle(Engine e) => (e == Engine.Linux ? Linux : Windows).IsRunning
        ? Run($"Stopping {Engines.Name(e)} engine", () => engines.Stop(e))
        : Run($"Starting {Engines.Name(e)} engine", () => engines.Start(e));

    async Task RefreshState()
    {
        var (linux, windows) = await Task.Run(() => (engines.IsRunning(Engine.Linux), engines.IsRunning(Engine.Windows)));
        active = Engines.ActiveEngine();
        Linux.IsRunning = linux;
        Windows.IsRunning = windows;
        Linux.IsActive = active == Engine.Linux;
        Windows.IsActive = active == Engine.Windows;
        Linux.Subtitle = settings.Distro;
        Raise(nameof(Active), nameof(IsLinuxActive), nameof(IsWindowsActive));
        StateChanged?.Invoke();
    }

    async Task CheckUpdates(bool includeWsl)
    {
        var targets = Components.Where(c => includeWsl || c.Component != Component.WslEngine).ToList();
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
            var installed = await Updates.Installed(c.Component, settings.Distro);
            c.Installed = installed?.ToString() ?? "–";
            var latest = await Updates.Latest(c.Component, settings.Distro);
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
        var machine = items.Where(i => i.Component is Component.Engine or Component.Compose or Component.Buildx).ToList();
        if (machine.Count > 0)
            await Elevation.Run(Log, "--machine-update", string.Join(',', machine.Select(m => m.Component)));

        if (items.Any(i => i.Component == Component.WslEngine))
            await UserSetup.UpdateWsl(Log, settings.Distro);

        foreach (var item in items.Where(i => i.Component != Component.App)) await Check(item);

        if (items.FirstOrDefault(i => i.Component == Component.App) is { LatestRelease: { } release })
        {
            Log($"Downloading Dockyard {release.Tag}");
            var exe = await Updates.DownloadApp(release);
            Elevation.StartDetached("--machine-update", nameof(Component.App), "--source", exe);
            Log("Restarting Dockyard");
            ShutdownRequested?.Invoke();
        }
    });

    Task Install() => Run("Installing", async () =>
    {
        var distro = SelectedDistro ?? throw new InvalidOperationException("Select a WSL distro");
        Log("Administrator step");
        var reboot = await Elevation.Run(Log, "--machine-install");
        Log("User step");
        await UserSetup.Install(Log, distro);
        settings.Distro = distro;
        IsInstalled = true;

        if (!Paths.IsInstalledCopy) Shell.StartUnelevated(Paths.AppExe);
        Log(reboot
            ? "Done. Reboot to finish enabling Windows containers."
            : "Done. Dockyard runs in the tray. Sign out and back in once so docker-users applies to the Windows engine.");
        Status = "Installed";
    });

    Task Uninstall() => Run("Uninstalling", async () =>
    {
        engines.ReleaseKeepAlive();
        await UserSetup.Uninstall(Log);
        await Elevation.Run(Log, "--machine-uninstall");
        IsInstalled = false;
        Log("Dockyard and both Docker engines were removed.");
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
