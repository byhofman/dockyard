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

    public static bool IsServer => IsServerSku(InstallationType());

    public static bool IsServerSku(string? installationType) => installationType is "Server" or "Server Core";
}
