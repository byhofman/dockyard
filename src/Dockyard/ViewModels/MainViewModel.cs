using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.Hosts;
using Dockyard.Setup;
using static Dockyard.Setup.SettingDefinitions;

namespace Dockyard.ViewModels;

// The Toolbox's window and tray over DockyardSetup: what it shows and when, never what a choice does.
sealed class MainViewModel : Observable
{
    readonly DockyardSetup setup;
    public DockyardSetup Setup => setup;
    readonly List<Command> commands = [];
    readonly StringBuilder log = new();
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly Func<Task<bool>> openInTray;
    readonly IProgress<SetupProgress> progress;
    string? busy;
    string? lastError;
    string status = "Ready";
    bool isSetupMode;
    bool isInstalled;
    bool removeData;
    bool needsReboot;
    string installedVersion = "–";
    readonly SetupOptions options;
    string active;

    public MainViewModel(DockyardSetup setup, bool setupMode, SetupOptions? options = null, Func<Task<bool>>? openInTray = null)
    {
        this.setup = setup;
        this.openInTray = openInTray ?? (() => setup.ShowInstalledWindow(TimeSpan.FromSeconds(20)));
        this.options = options ?? new SetupOptions();
        progress = new ReportTo(p => Log(p.Line));
        isSetupMode = setupMode;
        active = setup.Engines[0].Id;
        setup.ExitRequested += () => ShutdownRequested?.Invoke();
        setup.Duties.Checks += checks => Dispatcher.UIThread.Post(() => ShowQuietly(checks));

        EngineCards = setup.Engines.Select(e => new EngineViewModel(e)
        {
            ToggleCommand = Cmd(() => Toggle(e.Id)),
            UseCommand = Cmd(() => Switch(e.Id)),
            SettingsCommand = new Command(() => { EngineSettingsRequested?.Invoke(e); return Task.CompletedTask; }),
        }).ToList();

        foreach (var row in setup.Components())
        {
            ComponentViewModel? item = null;
            item = new ComponentViewModel(row, setup.Flag(row.Id))
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
        foreach (var c in Components.Where(c => c.Flag is { } flag && this.options.Without.Contains(flag))) c.Include = false;
        // Until a check says otherwise, which automatic checks skip for an engine that is stopped.
        foreach (var c in Components) c.IsPresent = setup.IsPresent(c.Id);
        RefreshDashboard();

        CheckUpdatesCommand = Cmd(() => Run("Checking for updates", () => CheckUpdates(all: true)));
        UpdateAllCommand = Cmd(() => UpdateComponents(Components.Where(c => c.IsAvailable).ToList()), () => Components.Any(c => c.IsAvailable));
        InstallCommand = Cmd(Install);
        UninstallCommand = Cmd(Uninstall);
        RebootCommand = Cmd(() => { setup.Reboot(); return Task.CompletedTask; });
        ImportCommand = Cmd(() => Run("Importing from Docker Desktop", () => setup.Import(progress)));
        EnableHttpsCommand = Cmd(() => Run("Turning on HTTPS for container domains", () => ChangeSetting(TrustedHttps, On)));
        DisableHttpsCommand = Cmd(() => Run("Turning off HTTPS for container domains", () => ChangeSetting(TrustedHttps, Off)));

        timer.Tick += async (_, _) => await RefreshState();
    }

    readonly CancellationTokenSource trayLifetime = new();

    public void StartPolling() => timer.Start();

    public event Action? StateChanged;
    public event Action? ShutdownRequested;
    public event Action? HandedOver;

    public IReadOnlyList<EngineViewModel> EngineCards { get; }
    public ObservableCollection<ComponentViewModel> Components { get; } = [];
    public IReadOnlyList<ComponentViewModel> DockyardComponents => Components.Where(c => c.IsDockyard).OrderBy(c => c == Dashboard).ToList();
    public IReadOnlyList<ComponentViewModel> DockerComponents => Components.Where(c => !c.IsDockyard && !c.IsKubernetes).ToList();
    public IReadOnlyList<ComponentViewModel> KubernetesComponents => Components.Where(c => c.IsKubernetes).ToList();

    public Command CheckUpdatesCommand { get; }
    public Command UpdateAllCommand { get; }
    public Command InstallCommand { get; }
    public Command UninstallCommand { get; }
    public Command RebootCommand { get; }
    public Command ImportCommand { get; }
    public Command EnableHttpsCommand { get; }
    public Command DisableHttpsCommand { get; }

    bool IsOn(string key) => setup.GetSetting(key) == On;

    // A setting the facade validates, saves and applies; a refusal is reported and the switch goes back.
    async Task ChangeSetting(string key, string value)
    {
        await setup.SetSetting(key, value, progress);
        Raise(nameof(HttpsSwitch), nameof(ContainerDomains), nameof(KubectlShell), nameof(Source), nameof(SourceIndex), nameof(StartActiveAtLogon), nameof(StopOtherOnSwitch));
    }

    // Saved and applied as it changes, also while something else runs; a refusal is logged and the switch goes back.
    async void Save(string key, bool on, string property)
    {
        try { await ChangeSetting(key, on ? On : Off); }
        catch (Exception e) { Log("✖ " + e.Message); }
        finally { Raise(property); }
    }

    // A switch under Settings: the commands do the work, and a declined approval puts it back.
    public bool HttpsSwitch
    {
        get => IsOn(TrustedHttps);
        set
        {
            if (value == IsOn(TrustedHttps)) return;
            _ = (value ? EnableHttpsCommand : DisableHttpsCommand).ExecuteAsync().ContinueWith(_ => Raise(nameof(HttpsSwitch)), TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
    public bool ShowImport => ShowManage && DockerDesktop.IsInstalled;
    public ComponentViewModel Dashboard { get; }

    public bool ShowEngineConfiguration => ShowManage;
    // The window that edits an engine's daemon.json, opened from its card.
    public event Action<EngineInfo>? EngineSettingsRequested;

    public bool IncludeDashboard { get => Dashboard.Include; set { Dashboard.Include = value; Raise(nameof(IncludeDashboard)); } }
    public IEnumerable<ComponentViewModel> OptionalChoices => Components.Where(c => c.IsOptional);
    public string RequiredSummary => string.Join("\n", Components.Where(c => c.Row.RequiredBecause is not null).Select(c => $"{c.Title}: {c.Row.RequiredBecause}"));
    public bool RemoveData { get => removeData; set => Set(ref removeData, value); }
    public bool HasDashboard => setup.IsPresent(ComponentRules.DashboardId);
    public bool NeedsReboot { get => needsReboot; private set => Set(ref needsReboot, value); }
    public string InstalledVersion { get => installedVersion; private set { if (Set(ref installedVersion, value)) Raise(nameof(ManageText), nameof(ReinstallText)); } }
    public string ManageText => InstalledVersion == Paths.AppVersion
        ? $"Dockyard {InstalledVersion} is installed. Running the setup again keeps your images, containers and settings."
        : $"Dockyard {InstalledVersion} is installed. Update brings it to {Paths.AppVersion} and keeps your images, containers and settings.";
    public string ReinstallText => InstalledVersion == Paths.AppVersion ? "Run the setup again" : $"Update to {Paths.AppVersion}";
    public string WindowTitle => IsSetupMode ? "Dockyard Setup" : "Dockyard Toolbox";

    public string Version => "v" + Paths.AppVersion;
    public string Active => active;
    public EngineViewModel ActiveEngine => EngineCards.FirstOrDefault(e => e.Id == active) ?? EngineCards[0];
    // Two engines only on Windows, and only while both are installed: one removed leaves nothing to switch to.
    public bool HasEngineSwitch => IsDashboard && EngineCards.Count(e => e.IsAvailable) > 1;

    public string SetupDescription => OperatingSystem.IsWindows()
        ? "Installs the Windows Docker engine (dockerd service), the docker CLI with compose and buildx, a dedicated 'dockyard' WSL distro for Linux containers, and Dockyard itself, which runs in the tray."
        : OperatingSystem.IsMacOS()
            ? "Installs the docker CLI with compose and buildx, a small Linux VM for containers that starts when you need it, and Dockyard itself, which runs in the menu bar."
            : "Installs Docker CE with compose and buildx from Docker's repository, starts it on demand, and Dockyard itself, which runs in the tray.";

    public bool IsSetupMode
    {
        get => isSetupMode;
        private set { if (Set(ref isSetupMode, value)) Raise(nameof(IsDashboard), nameof(WindowTitle), nameof(HasEngineSwitch), nameof(ShowInstall), nameof(ShowManage), nameof(ShowWizardManage), nameof(ShowEngineConfiguration), nameof(ShowImport)); }
    }

    public bool IsDashboard => !IsSetupMode;
    public bool IsInstalled
    {
        get => isInstalled;
        private set { if (Set(ref isInstalled, value)) Raise(nameof(ShowInstall), nameof(ShowManage), nameof(ShowWizardManage), nameof(ShowEngineConfiguration), nameof(ShowImport)); }
    }

    // Setup installs, or brings an install to its own version; the installed app's window is where it is managed.
    public bool ShowInstall => IsSetupMode && !IsInstalled;
    public bool ShowManage => !IsSetupMode;
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
    public bool HasLog => log.Length > 0;
    // The log stays out of the way: the footer shows the step at hand, and a click on it opens the rest.
    bool showLog;
    public bool ShowLog { get => showLog; set => Set(ref showLog, value); }
    public Command ToggleLogCommand => toggleLog ??= new Command(() => { ShowLog = !ShowLog; return Task.CompletedTask; });
    Command? toggleLog;

    // Before the first install the choices are the window's own, and go to the facade with the install.
    ToolSource? chosenSource;
    bool? chosenKubectlShell;

    public bool StopOtherOnSwitch
    {
        get => IsOn(StopOtherEngineOnSwitch);
        set => Save(StopOtherEngineOnSwitch, value, nameof(StopOtherOnSwitch));
    }

    public ToolSource Source
    {
        get => chosenSource ?? (setup.GetSetting(SettingDefinitions.Source) == DockyardSource ? ToolSource.Dockyard : ToolSource.PackageManager);
        set
        {
            if (!IsInstalled)
            {
                chosenSource = value;
                Raise(nameof(Source), nameof(SourceIndex));
                return;
            }
            var choice = value == ToolSource.PackageManager ? PackageManager : DockyardSource;
            _ = Run(value == ToolSource.PackageManager ? "Moving the tools to the package manager" : "Moving the tools to Dockyard", async () =>
            {
                await ChangeSetting(SettingDefinitions.Source, choice);
                await RefreshState();
            });
        }
    }

    // A setting the dashboard pointed at, marked for a few seconds; the window scrolls to it.
    string? highlighted;
    public string? Highlighted { get => highlighted; private set { if (Set(ref highlighted, value)) Raise(nameof(HighlightDomains), nameof(HighlightHttps)); } }
    public bool HighlightDomains => highlighted is "domains" or "proxy";
    public bool HighlightHttps => highlighted is "https" or "proxy";

    public void Highlight(string setting)
    {
        Highlighted = setting;
        var marked = setting;
        DispatcherTimer.RunOnce(() => { if (Highlighted == marked) Highlighted = null; }, TimeSpan.FromSeconds(6));
    }

    // On Windows a setting can be the Linux engine's alone; it says so on its label, where it is seen before it is turned on.
    static string LinuxEngineOnly(string key) =>
        All.First(d => d.Key == key) is var d && d.LinuxEngineOnly && OperatingSystem.IsWindows() ? $"{d.Title} (Linux engine only)" : d.Title;
    public static string DomainsLabel { get; } = LinuxEngineOnly(SettingDefinitions.ContainerDomains);
    public static string HttpsLabel { get; } = LinuxEngineOnly(TrustedHttps);

    public bool ContainerDomains
    {
        get => IsOn(SettingDefinitions.ContainerDomains);
        set { if (value != IsOn(SettingDefinitions.ContainerDomains)) Save(SettingDefinitions.ContainerDomains, value, nameof(ContainerDomains)); }
    }

    public bool KubectlShell
    {
        get => chosenKubectlShell ?? IsOn(SettingDefinitions.KubectlShell);
        set
        {
            if (!IsInstalled)
            {
                chosenKubectlShell = value;
                Raise(nameof(KubectlShell));
                return;
            }
            Save(SettingDefinitions.KubectlShell, value, nameof(KubectlShell));
        }
    }

    public static string ShellSetupLabel =>
        $"Set up kubectl in my shell: k, completion, {(OperatingSystem.IsWindows() ? "@do" : "$do")} for --dry-run=client -o yaml and {(OperatingSystem.IsWindows() ? "@now" : "$now")} for --force --grace-period 0";

    public static string ShellProfilesText => All.First(d => d.Key == SettingDefinitions.KubectlShell).Description;

    public static IReadOnlyList<string> ToolSources => All.First(d => d.Key == SettingDefinitions.Source).Choices!;

    public int SourceIndex
    {
        get => Source == ToolSource.PackageManager ? 0 : 1;
        set { Source = value == 0 ? ToolSource.PackageManager : ToolSource.Dockyard; Raise(nameof(SourceIndex)); }
    }

    public bool StartActiveAtLogon
    {
        get => IsOn(StartEngineAtLogon);
        set => Save(StartEngineAtLogon, value, nameof(StartActiveAtLogon));
    }

    public async Task StartTray()
    {
        IsInstalled = setup.IsInstalled;
        await RefreshState();
        await Run("Starting Dockyard", () => setup.Duties.Start(progress, trayLifetime.Token));
    }

    public async Task LoadSetup()
    {
        IsInstalled = setup.IsInstalled;
        if (IsInstalled) ChooseWhatIsThere();
        await RefreshState();
        if (options.Resume)
        {
            await Run("Finishing the setup", async () =>
            {
                await RunSetup(options.Request() with { Start = true });
                IsInstalled = true;
            });
        }
        if (IsInstalled) await Run("Checking for updates", () => CheckUpdates(all: false));
    }

    // Over an existing install the boxes start from what it has, then what the command line asked for.
    void ChooseWhatIsThere()
    {
        foreach (var c in Components.Where(c => c.IsOptional && c != Dashboard))
            c.Include = c.Flag is { } flag && options.With.Contains(flag)
                || setup.IsPresent(c.Id) && !(c.Flag is { } without && options.Without.Contains(without));
        IncludeDashboard = options.Dashboard ?? setup.IsPresent(ComponentRules.DashboardId);
    }

    // Its version comes from the update check; whether it is there at all is known without one, as soon as the window opens.
    void RefreshDashboard()
    {
        Dashboard.IsPresent = HasDashboard;
        if (Dashboard.State is UpdateState.Unknown or UpdateState.OptionalInstalled or UpdateState.OptionalAbsent || !Dashboard.IsPresent)
            Dashboard.State = HasDashboard ? UpdateState.OptionalInstalled : UpdateState.OptionalAbsent;
    }

    public async Task OnWindowShown()
    {
        await RefreshState();
        if (!IsSetupMode && !IsBusy && setup.Duties.CheckIsDue)
            await Run("Checking for updates", () => CheckUpdates(all: false));
    }

    public Task Switch(string engine) => Run($"Switching to {Name(engine)} containers", () => setup.UseEngine(engine));

    // What the dashboard waits for after asking for a switch: null when it went through.
    public async Task<string?> SwitchFor(string engine)
    {
        if (Busy is { } busy) return $"Dockyard is busy: {busy.ToLowerInvariant()}. Try again when it is done.";
        lastError = null;
        await Switch(engine);
        return lastError;
    }

    public Task StartEngine(string engine) => Run($"Starting {Name(engine)} engine", () => setup.StartEngine(engine));

    public void Shutdown()
    {
        timer.Stop();
        trayLifetime.Cancel();
        setup.Dispose();
    }

    string Name(string engine) => EngineCards.FirstOrDefault(e => e.Id == engine)?.Name ?? engine;

    async Task Toggle(string engine)
    {
        if (EngineCards.First(e => e.Id == engine).IsRunning)
            await Run($"Stopping {Name(engine)} engine", () => setup.StopEngine(engine));
        else
            await Run($"Starting {Name(engine)} engine", () => setup.StartEngine(engine));
    }

    async Task RefreshState()
    {
        foreach (var e in EngineCards) e.IsRunning = await setup.IsRunning(e.Id);
        active = setup.ActiveEngine();
        foreach (var e in EngineCards) e.IsActive = e.Id == active;
        RefreshDashboard();
        foreach (var e in EngineCards) e.IsAvailable = e.Info.ComponentId is not { } id || setup.IsPresent(id);
        Raise(nameof(HasEngineSwitch));
        Raise(nameof(Active), nameof(ActiveEngine));
        StateChanged?.Invoke();
    }

    async Task CheckUpdates(bool all, IReadOnlyCollection<string>? ids = null)
    {
        var checks = await setup.Check(ids, skipStoppedEngines: !all, CancellationToken.None);
        foreach (var check in checks) Show(check);
        UpdateAllCommand.Refresh();
        Raise(nameof(UpdateOffer), nameof(HasUpdates));
        StateChanged?.Invoke();
        if (ids is not null) return;

        var available = Components.Count(c => c.IsAvailable);
        Log(available > 0 ? $"{available} update(s) available"
            : checks.All(c => c.Error is null) ? "Everything is up to date"
            : "Not everything could be checked; try again later");
    }

    // The background duties look for updates by themselves; the rows follow, without the busy state that would block the tray's menu.
    void ShowQuietly(IReadOnlyList<DockyardSetup.ComponentCheck> checks)
    {
        if (IsSetupMode) return;
        foreach (var check in checks) Show(check);
        UpdateAllCommand.Refresh();
        Raise(nameof(UpdateOffer), nameof(HasUpdates));
        StateChanged?.Invoke();
    }

    public bool HasUpdates => Components.Any(c => c.IsAvailable);
    public string? UpdateOffer => UpdateOffers.Text(Components.Where(c => c.IsAvailable).Select(c => (c.Id, c.Title, c.Latest)).ToList());

    void Show(DockyardSetup.ComponentCheck check)
    {
        var c = Components.First(c => c.Id == check.Row.Id);
        c.Installed = check.Row.Installed ?? "–";
        c.Source = check.Row.Installed is null ? null : check.Row.Source;
        if (c.Id == nameof(Component.Tray)) InstalledVersion = c.Installed;
        c.IsPresent = check.IsPresent;
        c.LatestRelease = check.Latest;
        c.Latest = check.Row.Newest ?? "–";
        c.Error = check.Error;
        if (check.Error is not null) Log($"{c.Title}: {check.Error}");
        c.State = check.Error is not null ? check.CouldNotCheck ? UpdateState.CouldNotCheck : UpdateState.Error
            : !c.IsPresent && c.IsOptional ? UpdateState.OptionalAbsent
            : check.Row.Installed is null ? c.IsOptional ? UpdateState.OptionalInstalled : UpdateState.NotInstalled
            : check.Row.Newest is null ? UpdateState.NoRelease
            : check.Row.CanUpdate ? UpdateState.Available
            : UpdateState.UpToDate;
        c.UpdateCommand.Refresh();
    }

    Task UpdateComponents(IReadOnlyList<ComponentViewModel> items) => Run("Updating", async () =>
    {
        // The wizard is the new version: installing it also does what the jump from an old version needs, such as
        // moving to the dockyard distro, which replacing only the app would skip.
        if (IsSetupMode && items.Any(i => i.Id == nameof(Component.Tray)))
        {
            await InstallCore();
            items = items.Where(i => i.Id != nameof(Component.Tray)).ToList();
        }
        if (items.Count == 0) return;
        await setup.Update(items.Select(i => i.Id).ToList(), progress);
        await CheckUpdates(all: true, items.Select(i => i.Id).ToList());
    });

    Task Install() => Run("Installing", InstallCore);

    async Task InstallCore()
    {
        var optional = Components.Where(c => c.Flag is not null).ToList();
        var request = options.Request() with
        {
            With = optional.Where(c => c.Include).Select(c => c.Flag!).ToList(),
            Without = optional.Where(c => !c.Include).Select(c => c.Flag!).ToList(),
            Source = chosenSource ?? options.Source,
            KubectlShell = chosenKubectlShell ?? options.KubectlShell ?? false,
            Silent = false,
            Start = false,
        };
        var code = await RunSetup(request);
        var reboot = code == DockyardSetup.RebootRequired;
        chosenSource = null;
        chosenKubectlShell = null;
        IsInstalled = true;
        NeedsReboot = reboot;
        RefreshDashboard();
        var handedOver = !reboot && options.Start && await openInTray();
        Status = reboot ? "Restart to finish" : "Installed";
        if (handedOver)
        {
            HandedOver?.Invoke();
            return;
        }
        await CheckUpdates(all: true, Components.Select(c => c.Id).ToList());
    }

    async Task<int> RunSetup(SetupRequest request)
    {
        string? failure = null;
        var code = await setup.Setup(request, new ReportTo(p =>
        {
            if (p.Kind == SetupProgressKind.Error) failure ??= p.Line;
            progress.Report(p);
        }), CancellationToken.None);
        if (code is DockyardSetup.Failed or DockyardSetup.WrongArguments) throw new InvalidOperationException(failure ?? "Setup failed");
        return code;
    }

    // Rules first: what is required, or the last engine, is refused with the reason and nothing changes.
    async Task SetComponent(ComponentViewModel item, bool installed)
    {
        await Run($"{(installed ? "Adding" : "Removing")} {item.Title}", async () =>
        {
            if (installed) await setup.Add(item.Id, progress);
            else await setup.Remove(item.Id, removeData: false, progress);
            await CheckUpdates(all: true, [item.Id]);
            RefreshDashboard();
            Raise(nameof(HasDashboard));
        });
    }

    Task Uninstall() => Run("Uninstalling", async () =>
    {
        await setup.Uninstall(RemoveData, progress);
        IsInstalled = false;
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
            lastError = ex.Message;
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
        if (Busy is not null) Status = line;
        Raise(nameof(LogText), nameof(HasLog));
    }

    Command Cmd(Func<Task> run, Func<bool>? canRun = null)
    {
        var command = new Command(run, () => Busy is null && (canRun?.Invoke() ?? true));
        commands.Add(command);
        return command;
    }
}
