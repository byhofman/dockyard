using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Installing, changing and uninstalling on Windows. The machine-wide part runs in MachineSetup, in this process when it is
// elevated and otherwise through an elevated copy of it; the rest is the user's, in UserSetup.
[SupportedOSPlatform("windows")]
static class WindowsSetup
{
    public static async Task<bool> MachineInstall(Action<string> log, bool dashboard, IReadOnlyCollection<string> without)
    {
        if (!Elevation.IsElevated)
        {
            string[] extra = [.. dashboard ? Array.Empty<string>() : ["--engine-only"], .. without.Count > 0 ? new[] { "--without", string.Join(',', without) } : []];
            return await Elevation.Run(log, "--machine-install", extra);
        }
        var (sid, name) = Elevation.CurrentUser;
        return await MachineSetup.Install(log, sid, name, Environment.ProcessId, engineOnly: !dashboard, without);
    }

    public static async Task MachineUninstall(Action<string> log)
    {
        if (Elevation.IsElevated) await MachineSetup.Uninstall(log, Environment.ProcessId);
        else await Elevation.Run(log, "--machine-uninstall");
    }

    public static async Task MachineComponent(Action<string> log, string component, bool installed)
    {
        if (Elevation.IsElevated)
        {
            var (sid, name) = Elevation.CurrentUser;
            await MachineSetup.SetComponent(log, Enum.Parse<Component>(component), installed, sid, name);
        }
        else await Elevation.Run(log, "--machine-component", component, installed ? "add" : "remove");
    }

    public static async Task SetDashboard(Action<string> log, bool installed)
    {
        if (Elevation.IsElevated) await MachineSetup.SetDashboard(log, installed, Environment.ProcessId);
        else await Elevation.Run(log, "--machine-dashboard", installed ? "add" : "remove");
    }

    public static async Task UpdateDashboard(Action<string> log, Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            log($"Downloading the dashboard {release.Tag}");
            await Updates.DownloadReleaseAsset(Paths.DashboardZipName, dir, tag: release.Tag);
            if (Elevation.IsElevated) await MachineSetup.UpdateDashboard(log, dir, Environment.ProcessId);
            else await Elevation.Run(log, "--machine-dashboard", "update", "--source-dir", dir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // The machine's store: the current user's would show its own dialog from an elevated process nobody is watching, and
    // the wizard's administrator approval is the prompt that explains itself.
    public static async Task<bool> TrustCa(Action<string> log, string certificateFile)
    {
        try
        {
            var pem = await File.ReadAllTextAsync(certificateFile);
            if (Elevation.IsElevated) MachineTrust.TrustCa(log, pem);
            // The certificate itself and not its path: approval can come from another administrator's account, which
            // cannot read this user's owner-only folder.
            else await Elevation.Run(log, "--machine-trust-ca", MachineTrust.EncodeCertificate(pem));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    public static async Task UntrustCa(Action<string> log, string certificateFile)
    {
        if (Elevation.IsElevated) MachineTrust.UntrustCa(log);
        else if (File.Exists(certificateFile)) await Elevation.Run(log, "--machine-untrust-ca", "all");
    }
}
