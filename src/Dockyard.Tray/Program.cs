using Avalonia;
using Avalonia.Controls;
using Dockyard.Setup;
using Dockyard.Shared;

namespace Dockyard.Tray;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine((System.Reflection.Assembly.GetEntryAssembly()?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0").Split('+')[0]);
            return 0;
        }
        using var instance = TrayInstance.TryTake();
        if (instance is null) return 0;
        // The login item can start while the screen is locked; a tray has no reason to give up waiting for it.
        DisplayGate.WaitUntilUsable(Timeout.InfiniteTimeSpan);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TrayApp>()
        .UsePlatformDetect()
        // The default dock policy overrides LSUIElement with a Dock icon, and Avalonia's app delegate activates the app when it
        // finishes launching, which would take keyboard focus from whatever the user is doing at every tray start.
        .With(new MacOSPlatformOptions { ShowInDock = false, DisableAvaloniaAppDelegate = true })
        .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
        .With(new SkiaOptions { MaxGpuResourceSizeBytes = 8 * 1024 * 1024 });
}
