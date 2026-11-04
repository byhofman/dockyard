using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Dockyard.Dashboard.Shell;
using Dockyard.Shared;

namespace Dockyard.Dashboard;

public partial class App : Application
{
    public override void Initialize()
    {
        StartupTrace.Mark("platform initialized");
        AvaloniaXamlLoader.Load(this);
        StartupTrace.Mark("app resources and styles loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var vm = new ShellViewModel();
        var window = new MainWindow { DataContext = vm };
        StartupTrace.Mark("main window built");
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => vm.Stop();

        LocalPipe.Serve(LocalPipe.DashboardName, _ => Dispatcher.UIThread.Post(() =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }));

        window.Opened += (_, _) =>
        {
            StartupTrace.Mark("window opened");
            Dispatcher.UIThread.Post(vm.Start, DispatcherPriority.Background);
        };
        base.OnFrameworkInitializationCompleted();
    }
}
