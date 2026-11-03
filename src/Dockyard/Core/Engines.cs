using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

enum Engine { Linux, Windows }

sealed partial class Engines(Settings settings)
{
    // Holds a WSL session open; WSL stops an idle distro ~60s after its last session ends.
    Process? keepAlive;

    public static string Name(Engine e) => e == Engine.Linux ? "Linux" : "Windows";
    public static Engine Other(Engine e) => e == Engine.Linux ? Engine.Windows : Engine.Linux;

    public bool IsRunning(Engine e) => e == Engine.Linux ? IsLinuxRunning() : IsWindowsRunning();

    static bool IsLinuxRunning()
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync("127.0.0.1", Paths.LinuxPort).Wait(300) && client.Connected;
        }
        catch { return false; }
    }

    static bool IsWindowsRunning()
    {
        try
        {
            using var sc = new ServiceController(Paths.ServiceName);
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    public Task Start(Engine e) => e == Engine.Linux ? StartLinux() : StartWindows();
    public Task Stop(Engine e) => e == Engine.Linux ? StopLinux() : StopWindows();

    public async Task Switch(Engine target)
    {
        await Start(target);
        await UseContext(target);
        if (settings.StopOtherOnSwitch) await Stop(Other(target));
    }

    async Task StartLinux()
    {
        if (keepAlive is { HasExited: false } && IsLinuxRunning()) return;
        if (keepAlive is null || keepAlive.HasExited)
        {
            keepAlive?.Dispose();
            keepAlive = Shell.Spawn("wsl.exe", "-d", settings.Distro, "-u", "root", "--",
                "sh", "-c", "systemctl start docker && exec sleep infinity");
        }
        var proc = keepAlive;
        await WaitFor(IsLinuxRunning, TimeSpan.FromSeconds(90), "Linux engine", failed: () => proc.HasExited);
    }

    async Task StopLinux()
    {
        if (keepAlive is { HasExited: false } || IsLinuxRunning())
            await Shell.Check("wsl.exe", "-d", settings.Distro, "-u", "root", "--",
                "systemctl", "stop", "docker.service", "docker.socket", "containerd.service");
        ReleaseKeepAlive();
    }

    static async Task StartWindows()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            if (sc.Status == ServiceControllerStatus.Stopped) sc.Start();
            await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(60)));
        }
        await WaitFor(() => PipeExists(Paths.WindowsPipeName), TimeSpan.FromSeconds(30), "Windows engine");
    }

    static async Task StopWindows()
    {
        using var sc = new ServiceController(Paths.ServiceName);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        if (sc.Status != ServiceControllerStatus.StopPending) sc.Stop();
        await Task.Run(() => sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(60)));
    }

    public void ReleaseKeepAlive()
    {
        if (keepAlive is { HasExited: false }) keepAlive.Kill(entireProcessTree: true);
        keepAlive?.Dispose();
        keepAlive = null;
    }

    public static Engine ActiveEngine()
    {
        try
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(Paths.UserProfile, ".docker", "config.json")));
            return config?["currentContext"]?.GetValue<string>() == "win" ? Engine.Windows : Engine.Linux;
        }
        catch { return Engine.Linux; }
    }

    public static Task UseContext(Engine e) => Shell.Check(Paths.DockerExe, "context", "use", e == Engine.Windows ? "win" : "linux");

    static bool PipeExists(string name)
    {
        if (WaitNamedPipe($@"\\.\pipe\{name}", 1)) return true;
        const int ErrorSemTimeout = 121;
        return Marshal.GetLastPInvokeError() == ErrorSemTimeout;
    }

    static async Task WaitFor(Func<bool> ready, TimeSpan timeout, string what, Func<bool>? failed = null)
    {
        var until = DateTime.UtcNow + timeout;
        while (!await Task.Run(ready))
        {
            if (failed?.Invoke() == true) throw new InvalidOperationException($"{what} failed to start");
            if (DateTime.UtcNow > until) throw new System.TimeoutException($"{what} not ready after {timeout.TotalSeconds:0}s");
            await Task.Delay(500);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, int timeout);
}
