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

        using var mutex = new Mutex(true, @"Local\Dockyard", out var first);
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
            Console.WriteLine(uninstall ? "Dockyard was removed." : "Done. Sign out and back in once so the docker group applies.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    static AppBuilder Build(AppMode mode, string[]? args = null) => AppBuilder.Configure(() => new App(mode, args ?? [])).UsePlatformDetect();

    // Used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => Build(AppMode.Setup);
}
