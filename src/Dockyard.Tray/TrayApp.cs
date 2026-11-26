using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Dockyard.Setup;

namespace Dockyard.Tray;

// No window, no theme, no XAML: an icon and its menu.
sealed class TrayApp : Application
{
    readonly CancellationTokenSource lifetime = new();

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var setup = DockyardSetup.ForThisMachine();
        var tray = new TrayIcon { ToolTipText = "Dockyard" };
        var native = new NativeTrayMenu();
        tray.Menu = native.Menu;

        var controller = new TrayController(setup, () =>
        {
            lifetime.Cancel();
            tray.IsVisible = false;
            setup.Dispose();
            desktop.Shutdown();
        });
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
            }, image.Badge, image.Update);
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
