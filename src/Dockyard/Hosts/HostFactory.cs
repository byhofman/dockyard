namespace Dockyard.Hosts;

static class HostFactory
{
    public static IEngineHost Create() =>
        OperatingSystem.IsWindows() ? new WindowsHost(ProcessRunner.Instance)
        : throw new PlatformNotSupportedException("This platform has no Dockyard host yet");
}
