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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var vm = new ShellViewModel();
        var window = new MainWindow { DataContext = vm };
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => vm.Stop();

        LocalPipe.Serve(LocalPipe.DashboardName, _ => Dispatcher.UIThread.Post(() =>
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }));

        vm.Start();
        base.OnFrameworkInitializationCompleted();
    }
}
