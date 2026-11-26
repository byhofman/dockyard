using Avalonia;
using Avalonia.Controls;
using Dockyard.Shared;

namespace Dockyard.Dashboard;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Asked by the wizard on macOS and Linux to learn which version is installed, so this comes before anything with a window.
        if (args.Contains("--version"))
        {
            Console.WriteLine((System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0]);
            return 0;
        }
        Shell.StartupTrace.Mark("main");
        WatchFailures();
        var section = args.SkipWhile(a => a != "--section").Skip(1).FirstOrDefault();
        if (LocalPipe.TrySend(LocalPipe.DashboardName, section is null ? "show" : $"show {section}")) return 0;
        Shell.ShellViewModel.InitialSection = section;
        if (!DisplayGate.WaitUntilUsable(TimeSpan.FromMinutes(10))) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
    }

    // An error on the UI thread that nothing caught is logged and shown, and the dashboard stays up: it only reads and
    // asks the engine, so there is no half-written state to protect by ending it. Elsewhere it is at least logged.
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
