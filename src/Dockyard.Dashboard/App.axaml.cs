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
        // The headless tests have no desktop lifetime: they build the window themselves.
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        var vm = new ShellViewModel();
        var window = new MainWindow { DataContext = vm };
        if (ShellViewModel.InitialSection is { } first) vm.ShowSection(first);
        StartupTrace.Mark("main window built");
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => vm.Stop();

        // Closing hides it and it stays connected, so opening it again from the tray is instant; the tray's Quit ends it.
        var quitting = false;
        window.Closing += (_, e) =>
        {
            if (quitting || e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            window.Hide();
        };

        LocalPipe.Serve(LocalPipe.DashboardName, line => Dispatcher.UIThread.Post(() =>
        {
            if (EngineSwitch.ParseResult(line) is not null)
            {
                vm.OnTrayMessage(line);
                return;
            }
            if (line == "quit")
            {
                quitting = true;
                desktop.Shutdown();
                return;
            }
            if (line.StartsWith("show ", StringComparison.Ordinal)) vm.ShowSection(line[5..]);
            window.Show();
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
