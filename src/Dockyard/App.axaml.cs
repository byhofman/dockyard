using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.Engine;
using Dockyard.Engine.Transport;
using Dockyard.Shared;
using Dockyard.ViewModels;
using Dockyard.Views;
namespace Dockyard;

public partial class App : Application
{
    readonly AppMode mode;
    readonly string[] args;
    MainViewModel vm = null!;
    MainWindow? window;
    TrayIcon? tray;
    NativeMenuItem? dashboardItem;
    NativeMenuItem? updateItem;
    NativeMenuItemSeparator? updateSeparator;
    readonly List<(string Engine, NativeMenuItem Item)> engineItems = [];
    NativeMenuItem? containersItem;
    List<TrayContainer> shownContainers = [];
    bool refreshingContainers;
    IClassicDesktopStyleApplicationLifetime desktop = null!;

    public App() : this(AppMode.Setup, []) { }

    internal App(AppMode mode, string[] args)
    {
        this.mode = mode;
        this.args = args;
    }

    // The old name counts until the next wizard run replaces it: a tray that updated itself from before the rename can find it there.
    static readonly string[] DashboardFiles = OperatingSystem.IsWindows() ? ["Dockyard Dashboard.exe", "Dockyard.Dashboard.exe"] : ["Dockyard Dashboard", "Dockyard.Dashboard"];
    static string DashboardExe => DashboardFiles.Select(f => Path.Combine(AppContext.BaseDirectory, f)).FirstOrDefault(File.Exists) ?? Path.Combine(AppContext.BaseDirectory, DashboardFiles[0]);
    static bool HasDashboard => File.Exists(DashboardExe);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        vm = new MainViewModel(Hosts.HostFactory.Create(), setupMode: mode == AppMode.Setup, SetupOptions.Parse(args));
        vm.StartPolling();
        vm.ShutdownRequested += () => Dispatcher.UIThread.Post(Quit);

        if (mode == AppMode.Setup)
        {
            desktop.MainWindow = window = new MainWindow { DataContext = vm };
            vm.HandedOver += () => Dispatcher.UIThread.Post(Quit);
        }
        else
        {
            CreateTray();
            vm.StateChanged += UpdateTray;
            LocalPipe.Serve(LocalPipe.TrayName, line =>
            {
                if (TrayCommand.Parse(line) is { } command) Dispatcher.UIThread.Post(() => Handle(command));
            });
            _ = StartTray();
        }
        base.OnFrameworkInitializationCompleted();
    }

    async Task StartTray()
    {
        if (OperatingSystem.IsLinux() && !await HasTrayHost())
        {
            ShowWindow();
            vm.Note("No system tray found. On GNOME, install the AppIndicator extension to get the Dockyard tray icon; until then this window stays available.");
        }
        await vm.StartTray();
        if (args.Any(a => a is "--settings" or "--start" or "--switch")) Handle(TrayCommand.FromArgs(args));
    }

    static async Task<bool> HasTrayHost()
    {
        try
        {
            var r = await Shell.Run("gdbus", "call", "--session", "--dest", "org.freedesktop.DBus", "--object-path", "/org/freedesktop/DBus",
                "--method", "org.freedesktop.DBus.NameHasOwner", "org.kde.StatusNotifierWatcher");
            return r.ExitCode != 0 || r.Output.Contains("true");
        }
        catch (Exception)
        {
            return true;
        }
    }

    void Handle(TrayCommand command)
    {
        switch (command.Kind)
        {
            case TrayCommandKind.Show: OpenDashboardOrWindow(); break;
            case TrayCommandKind.Settings:
                if (command.Assets is { } folder && Directory.Exists(folder) && Updates.LocalAssets != folder)
                {
                    Updates.LocalAssets = folder;
                    vm.Note($"Installs and updates use the files in {folder}, as setup was given them");
                }
                ShowWindow();
                break;
            case TrayCommandKind.Start: _ = vm.StartEngine(command.Engine!); break;
            case TrayCommandKind.Switch: _ = SwitchAndReport(command.Engine!); break;
            case TrayCommandKind.Run: _ = Task.Run(() => RunAndReply(command.Reply!, command.Args!)); break;
        }
    }

    async Task RunAndReply(string reply, IReadOnlyList<string> args)
    {
        StreamWriter writer;
        try { writer = LocalPipe.OpenReply(reply); }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException) { return; }
        using (writer)
        {
            void Write(string line)
            {
                try { lock (writer) writer.WriteLine(line); }
                catch (IOException) { }
            }
            var code = await Cli.Execute(vm.Host, args, Write, Write);
            Write(LocalPipe.ExitLine + code);
        }
    }

    // The dashboard shows the switch until it is connected to the new engine; a failure only the tray knows.
    async Task SwitchAndReport(string engine)
    {
        var error = await vm.SwitchFor(engine);
        var line = error is null ? $"switched {engine}" : $"switch-failed {engine} {error.ReplaceLineEndings(" ")}";
        await Task.Run(() => LocalPipe.TrySend(LocalPipe.DashboardName, line));
    }

    void CreateTray()
    {
        var settings = new NativeMenuItem("Engines and settings…");
        settings.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            // The dashboard stays running when its window closes; quitting Dockyard ends it too.
            LocalPipe.TrySend(LocalPipe.DashboardName, "quit");
            Quit();
        };

        var menu = new NativeMenu();
        updateItem = new NativeMenuItem();
        updateItem.Click += (_, _) =>
        {
            ShowWindow();
            vm.UpdateAllCommand.Execute(null);
        };
        updateSeparator = new NativeMenuItemSeparator();
        if (vm.EngineCards.Count > 1)
        {
            foreach (var engine in vm.EngineCards)
            {
                var item = new NativeMenuItem(engine.Title) { ToggleType = MenuItemToggleType.Radio };
                var id = engine.Id;
                item.Click += (_, _) => _ = vm.Switch(id);
                engineItems.Add((id, item));
                menu.Add(item);
            }
        }
        else
        {
            var engine = vm.EngineCards[0];
            var item = new NativeMenuItem(engine.ToggleText + " engine");
            item.Click += (_, _) => engine.ToggleCommand.Execute(null);
            engineItems.Add((engine.Id, item));
            menu.Add(item);
        }
        containersItem = new NativeMenuItem("Containers") { IsVisible = false, Menu = new NativeMenu() };
        menu.Add(containersItem);
        menu.Add(new NativeMenuItemSeparator());
        dashboardItem = new NativeMenuItem("Open dashboard") { IsVisible = HasDashboard };
        dashboardItem.Click += (_, _) => OpenDashboard();
        menu.Add(dashboardItem);
        menu.Add(settings);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        tray = new TrayIcon { Menu = menu, ToolTipText = "Dockyard" };
        tray.Clicked += (_, _) => OpenDashboardOrWindow();
        TrayIcon.SetIcons(this, [tray]);
        UpdateTray();
    }

    void UpdateTray()
    {
        if (tray is null) return;
        // The dashboard can come and go while the tray runs, from its own window or from the wizard.
        if (dashboardItem is not null) dashboardItem.IsVisible = HasDashboard;
        var activeEngine = vm.ActiveEngine;
        var color = vm.IsBusy ? TrayColors.Busy : activeEngine.IsRunning ? TrayColors.Running : TrayColors.Stopped;
        var multiple = vm.EngineCards.Count > 1;
        var offer = vm.UpdateOffer;
        tray.Icon = TrayIconRenderer.Render(color, multiple ? activeEngine.Name[..1] : null, update: offer is not null);
        tray.ToolTipText = vm.IsBusy
            ? $"Dockyard: {vm.Busy}…"
            : "Dockyard\n" + string.Join("\n", vm.EngineCards.Select(e =>
                $"{e.Name} engine {e.StatusText.ToLowerInvariant()}{(multiple && e.IsActive ? " (active)" : "")}"))
                + (offer is null ? "" : "\nUpdate available, in the menu");
        if (updateItem is not null && updateSeparator is not null)
        {
            updateItem.Header = offer ?? "";
            // The Windows tray menu draws a hidden separator anyway, so the pair is added and removed instead.
            var menu = tray.Menu!;
            if (offer is not null && !menu.Items.Contains(updateItem))
            {
                menu.Items.Insert(0, updateItem);
                menu.Items.Insert(1, updateSeparator);
            }
            else if (offer is null && menu.Items.Contains(updateItem))
            {
                menu.Items.Remove(updateItem);
                menu.Items.Remove(updateSeparator);
            }
            updateItem.IsEnabled = !vm.IsBusy;
        }
        foreach (var (engine, item) in engineItems)
        {
            var card = vm.EngineCards.First(e => e.Id == engine);
            if (multiple) { item.IsChecked = card.IsActive; item.IsVisible = card.IsAvailable; }
            else item.Header = card.ToggleText + " engine";
            item.IsEnabled = !vm.IsBusy;
        }
        _ = RefreshContainers();
    }

    async Task RefreshContainers()
    {
        if (refreshingContainers || containersItem is null) return;
        refreshingContainers = true;
        try
        {
            List<TrayContainer> containers = [];
            if (vm.ActiveEngine.IsRunning)
            {
                using var docker = new DockerClient(DockerEndpoint.Parse(vm.ActiveEngine.Endpoint));
                containers = TrayContainers.From(await docker.ListContainersAsync(all: true));
            }
            if (!containers.SequenceEqual(shownContainers))
            {
                shownContainers = containers;
                BuildContainersMenu();
            }
        }
        catch (Exception e) when (e is DockerException or EngineUnavailableException or IOException) { }
        finally { refreshingContainers = false; }
    }

    void BuildContainersMenu()
    {
        var items = containersItem!.Menu!;
        items.Items.Clear();
        foreach (var container in shownContainers)
        {
            var actions = new NativeMenu();
            void Action(string header, Func<DockerClient, Task> run)
            {
                var item = new NativeMenuItem(header);
                item.Click += (_, _) => _ = ContainerAction(container, run);
                actions.Add(item);
            }
            if (container.Running)
            {
                Action("Stop", c => c.StopContainerAsync(container.Id));
                Action("Restart", c => c.RestartContainerAsync(container.Id));
                if (container.WebPort is { } port)
                {
                    var open = new NativeMenuItem($"Open http://127.0.0.1:{port}");
                    open.Click += (_, _) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}") { UseShellExecute = true })?.Dispose();
                    actions.Add(open);
                }
            }
            else Action("Start", c => c.StartContainerAsync(container.Id));

            var label = (container.Running ? "● " : "○ ") + (container.Project is { } project ? $"{project} / " : "") + container.Name;
            items.Add(new NativeMenuItem(label) { Menu = actions });
        }
        containersItem.IsVisible = shownContainers.Count > 0;
    }

    async Task ContainerAction(TrayContainer container, Func<DockerClient, Task> run)
    {
        try
        {
            using var docker = new DockerClient(DockerEndpoint.Parse(vm.ActiveEngine.Endpoint));
            await run(docker);
        }
        catch (Exception e) when (e is DockerException or EngineUnavailableException or IOException)
        {
            vm.Note($"{container.Name}: {e.Message}");
        }
        await RefreshContainers();
    }

    void OpenDashboardOrWindow()
    {
        if (HasDashboard) OpenDashboard();
        else ShowWindow();
    }

    void OpenDashboard()
    {
        if (LocalPipe.TrySend(LocalPipe.DashboardName, "show")) return;
        try
        {
            Process.Start(new ProcessStartInfo(DashboardExe) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            ShowWindow();
        }
    }

    void ShowWindow()
    {
        window ??= new MainWindow { DataContext = vm, HideOnClose = true };
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void Quit()
    {
        vm.Shutdown();
        if (tray is not null) tray.IsVisible = false;
        desktop.Shutdown();
    }
}
