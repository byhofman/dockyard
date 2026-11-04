namespace Dockyard.Hosts;

static class HostFactory
{
    public static IEngineHost Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsHost(ProcessRunner.Instance);
        if (OperatingSystem.IsLinux()) return new LinuxHost(ProcessRunner.Instance);
        if (OperatingSystem.IsMacOS()) return new MacHost(ProcessRunner.Instance);
        throw new PlatformNotSupportedException("Dockyard runs on Windows, Linux and macOS");
    }
}
