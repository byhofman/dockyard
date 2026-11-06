using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.Shared;
using Dockyard.ViewModels;
using Dockyard.Views;

namespace Dockyard;

enum AppMode { Setup, Tray }

public partial class App : Application
{
    readonly AppMode mode;
    readonly string[] args;
    MainViewModel vm = null!;
    MainWindow? window;
    TrayIcon? tray;
    NativeMenuItem? dashboardItem;
    readonly List<(string Engine, NativeMenuItem Item)> engineItems = [];
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
            case TrayCommandKind.Settings: ShowWindow(); break;
            case TrayCommandKind.Start: _ = vm.StartEngine(command.Engine!); break;
            case TrayCommandKind.Switch: _ = vm.Switch(command.Engine!); break;
        }
    }

    void CreateTray()
    {
        var settings = new NativeMenuItem("Engines and settings…");
        settings.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
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
        var color = vm.IsBusy ? TrayIconRenderer.Busy : activeEngine.IsRunning ? TrayIconRenderer.Running : TrayIconRenderer.Stopped;
        var multiple = vm.EngineCards.Count > 1;
        tray.Icon = TrayIconRenderer.Render(color, multiple ? activeEngine.Name[..1] : null);
        tray.ToolTipText = vm.IsBusy
            ? $"Dockyard: {vm.Busy}…"
            : "Dockyard\n" + string.Join("\n", vm.EngineCards.Select(e =>
                $"{e.Name} engine {e.StatusText.ToLowerInvariant()}{(multiple && e.IsActive ? " (active)" : "")}"));
        foreach (var (engine, item) in engineItems)
        {
            var card = vm.EngineCards.First(e => e.Id == engine);
            if (multiple) { item.IsChecked = card.IsActive; item.IsVisible = card.IsAvailable; }
            else item.Header = card.ToggleText + " engine";
            item.IsEnabled = !vm.IsBusy;
        }
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
