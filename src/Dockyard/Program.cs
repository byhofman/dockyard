using Avalonia;
using Avalonia.Controls;
using Dockyard.Core;

namespace Dockyard;

// One exe: run from Program Files it is the tray app; anywhere else (or with --setup) it is the installer;
// --install/--uninstall run setup unattended; --machine-* is the elevated half of setup.
static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Elevation.IsWorker(args)) return Elevation.Worker(args);
        if (Silent.IsSilent(args)) return Silent.Run(args);

        if (!(Paths.IsInstalledCopy || args.Contains("--tray")) || args.Contains("--setup"))
            return Build(AppMode.Setup).StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

        using var mutex = new Mutex(true, @"Local\Dockyard", out var first);
        if (!first)
        {
            if (EventWaitHandle.TryOpenExisting(App.ShowEventName, out var show)) show.Set();
            return 0;
        }
        return Build(AppMode.Tray).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    static AppBuilder Build(AppMode mode) => AppBuilder.Configure(() => new App(mode)).UsePlatformDetect();

    // Used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => Build(AppMode.Setup);
}
