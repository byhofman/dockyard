using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Dockyard.Core;
using Dockyard.ViewModels;
using Dockyard.Views;

namespace Dockyard;

enum AppMode { Setup, Tray }

public partial class App : Application
{
    public const string ShowEventName = @"Local\Dockyard.Show";

    readonly AppMode mode;
    MainViewModel vm = null!;
    MainWindow? window;
    TrayIcon? tray;
    NativeMenuItem? linuxItem;
    NativeMenuItem? windowsItem;
    IClassicDesktopStyleApplicationLifetime desktop = null!;

    public App() : this(AppMode.Setup) { }

    internal App(AppMode mode) => this.mode = mode;

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
            ListenForShow();
            _ = vm.StartTray();
        }
        base.OnFrameworkInitializationCompleted();
    }

    void CreateTray()
    {
        linuxItem = new NativeMenuItem("Linux containers") { ToggleType = MenuItemToggleType.Radio };
        linuxItem.Click += (_, _) => _ = vm.Switch(Engine.Linux);
        windowsItem = new NativeMenuItem("Windows containers") { ToggleType = MenuItemToggleType.Radio };
        windowsItem.Click += (_, _) => _ = vm.Switch(Engine.Windows);
        var open = new NativeMenuItem("Open Dockyard");
        open.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        tray = new TrayIcon
        {
            Menu = [linuxItem, windowsItem, new NativeMenuItemSeparator(), open, new NativeMenuItemSeparator(), quit],
            ToolTipText = "Dockyard",
        };
        tray.Clicked += (_, _) => ShowWindow();
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

    void ShowWindow()
    {
        window ??= new MainWindow { DataContext = vm, HideOnClose = true };
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    void ListenForShow()
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        new Thread(() =>
        {
            while (signal.WaitOne()) Dispatcher.UIThread.Post(ShowWindow);
        }) { IsBackground = true, Name = "Dockyard.Show" }.Start();
    }

    void Quit()
    {
        vm.Shutdown();
        if (tray is not null) tray.IsVisible = false;
        desktop.Shutdown();
    }
}
