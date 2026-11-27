using Avalonia;
using Avalonia.Controls;
using Dockyard.Shared;

namespace Dockyard.Dashboard;

static class Program
{
    [STAThread]
    public static int Main(string[] args) => Run(args, Launch.ForThisMachine(OpenWindow));

    // Setup's elevated step on Windows runs as the app that asked for it, and the command-line actions run without a
    // window: neither waits for a display, so both come before anything that does.
    public static int Run(string[] args, Launch launch)
    {
        if (launch.IsWorker(args)) return launch.Worker(args);
        // Asked by setup on macOS and Linux to learn which version is installed.
        if (args.Contains("--version"))
        {
            launch.AttachConsole();
            launch.Out.WriteLine((System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0]);
            return 0;
        }
        var command = global::Dockyard.Setup.SetupArguments.ParseCommand(args, out var problems);
        if (command is { Action: global::Dockyard.Setup.AppAction.Setup, Setup.Silent: false }) return launch.Window("setup");
        if (command is not null)
        {
            launch.AttachConsole();
            return Headless.Run(command, problems, launch);
        }
        return launch.Window(args.SkipWhile(a => a != "--section").Skip(1).FirstOrDefault());
    }

    static int OpenWindow(string? section)
    {
        Shell.StartupTrace.Mark("main");
        WatchFailures();
        if (LocalPipe.TrySend(LocalPipe.DashboardName, section is null ? "show" : $"show {section}")) return 0;
        Shell.ShellViewModel.InitialSection = section;
        if (!DisplayGate.WaitUntilUsable(TimeSpan.FromMinutes(10))) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([], ShutdownMode.OnMainWindowClose);
    }

    // An error on the UI thread that nothing caught is logged and shown, and the dashboard stays up: what it changes goes
    // through the engine or the setup facade, which keep their own state, so ending it protects nothing. Elsewhere it is
    // at least logged.
    static void WatchFailures()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Shell.Failures.Write(ex, "crash"); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Shell.Failures.Write(e.Exception, "background task"); e.SetObserved(); };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Shell.Failures.Report(e.Exception, "ui thread");
            e.Handled = true;
        };
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 8 * 1024 * 1024 });
}
