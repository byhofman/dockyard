using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dockyard.Setup;

namespace Dockyard.Tray;

// No window and no XAML: an icon and its menu.
sealed class TrayApp : Application
{
    readonly CancellationTokenSource lifetime = new();

    // Windows and Linux draw a tray icon's menu as Avalonia's own popup, which shows nothing without a theme; macOS uses
    // the system's menu. Compact, as a system tray menu is, not touch-sized.
    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme { DensityStyle = Avalonia.Themes.Fluent.DensityStyle.Compact });

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var setup = DockyardSetup.ForThisMachine();
        var tray = new TrayIcon { ToolTipText = "Dockyard" };
        var native = new NativeTrayMenu();
        tray.Menu = native.Menu;

        void Exit()
        {
            lifetime.Cancel();
            tray.IsVisible = false;
            setup.Dispose();
            desktop.Shutdown();
        }
        var controller = new TrayController(setup, Exit);
        // A dockyard command of its own is replacing or removing the tray; the dashboard stays.
        setup.ExitRequested += () => Dispatcher.UIThread.Post(Exit);
        tray.Clicked += (_, _) => controller.OpenDashboard(null);
        controller.Changed += () =>
        {
            native.Sync(controller.Menu);
            var image = controller.Menu.Icon;
            tray.Icon = TrayIconRenderer.Render(image.Level switch
            {
                TrayLevel.Busy => TrayColors.Busy,
                TrayLevel.Running => TrayColors.Running,
                _ => TrayColors.Stopped,
            }, image.Badge, image.Update, running: image.Level == TrayLevel.Running);
            tray.ToolTipText = controller.Menu.Tooltip;
        };
        TrayIcon.SetIcons(this, [tray]);

        setup.Duties.UpdatesFound += rows => Dispatcher.UIThread.Post(() => controller.UpdatesFound(rows));
        _ = setup.Duties.RunWhenFree(new Progress<SetupProgress>(controller.Report), startEngine: true, lifetime.Token);

        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        poll.Tick += async (_, _) => await controller.Refresh();
        poll.Start();
        _ = controller.Refresh();
        base.OnFrameworkInitializationCompleted();
    }
}
