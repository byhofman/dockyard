using Avalonia;
using Avalonia.Controls;
using Avalonia.Skia;
using Dockyard.Shared;

namespace Dockyard.Dashboard;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
