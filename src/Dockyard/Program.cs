using Avalonia;
using Avalonia.Controls;
using Dockyard.Core;
using Dockyard.Shared;

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
            LocalPipe.TrySend(LocalPipe.TrayName, TrayCommand.FromArgs(args).ToString(), timeoutMs: 3000);
            return 0;
        }
        return Build(AppMode.Tray, args).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    static AppBuilder Build(AppMode mode, string[]? args = null) => AppBuilder.Configure(() => new App(mode, args ?? [])).UsePlatformDetect();

    // Used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => Build(AppMode.Setup);
}
