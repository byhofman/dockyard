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
    NativeMenuItem? linuxItem;
    NativeMenuItem? windowsItem;
    IClassicDesktopStyleApplicationLifetime desktop = null!;

    public App() : this(AppMode.Setup, []) { }

    internal App(AppMode mode, string[] args)
    {
        this.mode = mode;
        this.args = args;
    }

    static string DashboardExe => Path.Combine(AppContext.BaseDirectory, "Dockyard.Dashboard.exe");
    static bool HasDashboard => File.Exists(DashboardExe);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        vm = new MainViewModel(setupMode: mode == AppMode.Setup);
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
        await vm.StartTray();
        if (args.Any(a => a is "--settings" or "--start" or "--switch")) Handle(TrayCommand.FromArgs(args));
    }

    void Handle(TrayCommand command)
    {
        switch (command.Kind)
        {
            case TrayCommandKind.Show: OpenDashboardOrWindow(); break;
            case TrayCommandKind.Settings: ShowWindow(); break;
            case TrayCommandKind.Start: _ = vm.StartEngine(ToEngine(command.Engine)); break;
            case TrayCommandKind.Switch: _ = vm.Switch(ToEngine(command.Engine)); break;
        }
    }

    static Engine ToEngine(string? id) => id == "win" ? Engine.Windows : Engine.Linux;

    void CreateTray()
    {
        linuxItem = new NativeMenuItem("Linux containers") { ToggleType = MenuItemToggleType.Radio };
        linuxItem.Click += (_, _) => _ = vm.Switch(Engine.Linux);
        windowsItem = new NativeMenuItem("Windows containers") { ToggleType = MenuItemToggleType.Radio };
        windowsItem.Click += (_, _) => _ = vm.Switch(Engine.Windows);
        var settings = new NativeMenuItem("Engines and settings…");
        settings.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu { linuxItem, windowsItem, new NativeMenuItemSeparator() };
        if (HasDashboard)
        {
            var dashboard = new NativeMenuItem("Open dashboard");
            dashboard.Click += (_, _) => OpenDashboard();
            menu.Add(dashboard);
        }
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
        var activeEngine = vm.Active == Engine.Linux ? vm.Linux : vm.Windows;
        var color = vm.IsBusy ? TrayIconRenderer.Busy : activeEngine.IsRunning ? TrayIconRenderer.Running : TrayIconRenderer.Stopped;
        tray.Icon = TrayIconRenderer.Render(color, vm.Active == Engine.Linux ? "L" : "W");
        tray.ToolTipText = vm.IsBusy
            ? $"Dockyard: {vm.Busy}…"
            : $"Dockyard: {Engines.Name(vm.Active)} containers\nLinux engine {vm.Linux.StatusText.ToLowerInvariant()}\nWindows engine {vm.Windows.StatusText.ToLowerInvariant()}";
        linuxItem!.IsChecked = vm.IsLinuxActive;
        windowsItem!.IsChecked = vm.IsWindowsActive;
        linuxItem.IsEnabled = windowsItem.IsEnabled = !vm.IsBusy;
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
