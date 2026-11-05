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
        if (OperatingSystem.IsWindows())
        {
            if (Elevation.IsWorker(args)) return Elevation.Worker(args);
            if (Silent.IsSilent(args)) return Silent.Run(args);
        }
        else if (args.Contains("--install") || args.Contains("--uninstall"))
        {
            return RunUnattended(args.Contains("--uninstall"), args.Contains("--engine-only"));
        }

        var installed = OperatingSystem.IsWindows() ? Paths.IsInstalledCopy : Hosts.HostFactory.Create().IsInstalled;
        if (!(installed || args.Contains("--tray")) || args.Contains("--setup"))
            return Build(AppMode.Setup).StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

        // On Unix "Local" means this login session, and launchd gives every app its own, so it would never see the running tray.
        using var mutex = OperatingSystem.IsWindows()
            ? new Mutex(true, @"Local\Dockyard", out var first)
            : new Mutex(true, "Dockyard", new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out first);
        if (!first)
        {
            LocalPipe.TrySend(LocalPipe.TrayName, TrayCommand.FromArgs(args).ToString(), timeoutMs: 3000);
            return 0;
        }
        return Build(AppMode.Tray, args).StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    static int RunUnattended(bool uninstall, bool engineOnly)
    {
        var host = Hosts.HostFactory.Create();
        try
        {
            if (uninstall) host.Uninstall(Console.WriteLine).GetAwaiter().GetResult();
            else host.Install(Console.WriteLine, engineOnly).GetAwaiter().GetResult();
            Console.WriteLine(uninstall ? "Dockyard was removed." : OperatingSystem.IsLinux() ? "Done. Sign out and back in once so the docker group applies." : "Done.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    static AppBuilder Build(AppMode mode, string[]? args = null) => AppBuilder.Configure(() => new App(mode, args ?? []))
        .UsePlatformDetect()
        // The default dock policy overrides LSUIElement and gives the tray a Dock icon and the app switcher entry of a foreground app.
        .With(new MacOSPlatformOptions { ShowInDock = mode == AppMode.Setup })
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 8 * 1024 * 1024 });

    // Used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => Build(AppMode.Setup);
}
