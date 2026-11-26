using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Dockyard.Dashboard.Shell;
using Dockyard.Setup;
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
        // One facade for the screens and the background duties alike.
        var setup = DockyardSetup.ForThisMachine();
        var vm = new ShellViewModel(new Features.Setup.DockyardSetupSource(setup));
        Failures.Show = vm.ShowError;
        var window = new MainWindow { DataContext = vm };
        if (ShellViewModel.InitialSection is { } first) vm.ShowSection(first);
        StartupTrace.Mark("main window built");
        desktop.MainWindow = window;
        // The tray runs the background duties while there is one; without it the dashboard does, for as long as it is open.
        var duties = new CancellationTokenSource();
        _ = setup.Duties.RunWhenFree(new Progress<SetupProgress>(vm.OnDuty), startEngine: false, duties.Token, yieldTo: () => TrayInstance.IsRunning);
        desktop.Exit += (_, _) =>
        {
            duties.Cancel();
            setup.Dispose();
            vm.Stop();
        };

        // With a tray running, closing hides the window and the dashboard stays connected, so opening it again is instant; the
        // tray's Quit ends it. Without a tray nothing could ever show or end a hidden dashboard, so closing ends it.
        var quitting = false;
        window.Closing += (_, e) =>
        {
            if (quitting || e.CloseReason != WindowCloseReason.WindowClosing || !TrayInstance.ClosingHidesDashboard()) return;
            e.Cancel = true;
            window.Hide();
        };
        // A dockyard command of its own is replacing or removing the dashboard.
        setup.ExitRequested += () => Dispatcher.UIThread.Post(() =>
        {
            quitting = true;
            desktop.Shutdown();
        });

        LocalPipe.Serve(LocalPipe.DashboardName, line => Dispatcher.UIThread.Post(() =>
        {
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
