using Avalonia;
using Avalonia.Controls;
using Dockyard.Shared;

namespace Dockyard.Dashboard;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (LocalPipe.TrySend(LocalPipe.DashboardName, "show")) return 0;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
