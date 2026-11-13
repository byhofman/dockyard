using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The Windows containers engine: dockerd as a Windows service, reached through its named pipe.
[SupportedOSPlatform("windows")]
static partial class WindowsServiceEngine
{
    public static bool IsRunning()
    {
        try
        {
            using var sc = new ServiceController(Paths.ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        // The service does not exist without the Windows engine.
        catch (InvalidOperationException) { return false; }
    }

    public static async Task Start()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)));
        }
        await Wait.For(() => PipeExists(Paths.WindowsPipeName), TimeSpan.FromSeconds(30), "Windows engine");
    }

    public static async Task Stop()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)));
    }

    static bool PipeExists(string name)
    {
        if (WaitNamedPipe($@"\\.\pipe\{name}", 1)) return true;
        const int ErrorSemTimeout = 121;
        return Marshal.GetLastPInvokeError() == ErrorSemTimeout;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, int timeout);
}
