using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Dockyard.Hosts;

[SupportedOSPlatform("windows")]
static class OsInfo
{
    public static string? InstallationType()
    {
        using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return version?.GetValue("InstallationType") as string;
    }
}
