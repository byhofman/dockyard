using Avalonia;
using Avalonia.Controls;
using Dockyard.Core;
using Dockyard.Shared;

namespace Dockyard;

// One exe: run from where it is installed it is the tray app; anywhere else (or with --setup) it is the wizard;
// --silent is the wizard without a window; --machine-* is the elevated half of setup.
static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && Elevation.IsWorker(args)) return Elevation.Worker(args);

        SetupOptions options;
        try
        {
            options = SetupOptions.Parse(args);
        }
        catch (SetupUsageException ex)
        {
            AttachParentConsole();
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(SetupOptions.Usage);
            return Installer.WrongArguments;
        }
        if (options.ShowVersion || options.ShowHelp)
        {
            AttachParentConsole();
            Console.WriteLine(options.ShowVersion ? Paths.AppVersion : SetupOptions.Usage);
            return 0;
        }
        if (options.Silent) return RunSilent(options);

        var host = Hosts.HostFactory.Create();
        var isTray = host.RunsFromInstallLocation && host.IsInstalled || args.Contains("--tray");
        if (!isTray || options.OpenSetup)
        {
            if (OperatingSystem.IsLinux() && !Hosts.LinuxLayout.HasDisplay(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            {
                Console.WriteLine("No display, so setup runs without a window.");
                return RunSilent(options);
            }
            return Build(AppMode.Setup, args).StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
        }

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

    static int RunSilent(SetupOptions options)
    {
        AttachParentConsole();
        Action<string> log = Console.WriteLine;
        Action<string> error = Console.Error.WriteLine;
        // A resume runs at logon with no console to report to.
        if (options.Resume && OperatingSystem.IsWindows())
        {
            var file = Path.Combine(Paths.SettingsDir, "resume.log");
            Directory.CreateDirectory(Paths.SettingsDir);
            log = line => File.AppendAllLines(file, [line]);
            error = log;
        }
        return Installer.RunSilent(Hosts.HostFactory.Create(), options, log, error).GetAwaiter().GetResult();
    }

    static void AttachParentConsole()
    {
        if (!OperatingSystem.IsWindows()) return;
        NativeConsole.AttachConsole(-1);
        Console.WriteLine();
    }

    static AppBuilder Build(AppMode mode, string[]? args = null) => AppBuilder.Configure(() => new App(mode, args ?? []))
        .UsePlatformDetect()
        // The default dock policy overrides LSUIElement with a Dock icon, and Avalonia's app delegate activates the app when it
        // finishes launching, which would take keyboard focus from whatever the user is doing at every tray start.
        .With(new MacOSPlatformOptions { ShowInDock = mode == AppMode.Setup, DisableAvaloniaAppDelegate = mode == AppMode.Tray })
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 8 * 1024 * 1024 });

    // Used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => Build(AppMode.Setup);
}

static partial class NativeConsole
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static partial bool AttachConsole(int processId);
}
