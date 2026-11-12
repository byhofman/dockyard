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
        if (LocalPipe.TrySend(LocalPipe.DashboardName, "show")) return 0;
        if (!DisplayGate.WaitUntilUsable(TimeSpan.FromMinutes(10))) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 8 * 1024 * 1024 });
}
