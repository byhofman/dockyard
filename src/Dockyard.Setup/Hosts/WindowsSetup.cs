using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// Installing, changing and uninstalling on Windows. The machine-wide part runs in MachineSetup, in this process when it is
// elevated and otherwise through an elevated copy of it; the rest is the user's, in UserSetup.
[SupportedOSPlatform("windows")]
static class WindowsSetup
{
    // The user's choice is read here and handed on: the administrator step may run under another account's settings.
    static ToolSource Source => Settings.Load().Source;
    static string[] SourceArgs => ["--source", Source.Argument()];

    public static async Task<bool> MachineInstall(Action<string> log, bool dashboard, IReadOnlyCollection<string> without)
    {
        if (!Elevation.IsElevated)
        {
            string[] extra =
            [
                .. dashboard ? Array.Empty<string>() : ["--engine-only"],
                .. without.Count > 0 ? new[] { "--without", string.Join(',', without) } : [],
                .. SourceArgs,
            ];
            return await Elevation.Run(log, "--machine-install", extra);
        }
        var (sid, name) = Elevation.CurrentUser;
        return await MachineSetup.Install(log, sid, name, Environment.ProcessId, engineOnly: !dashboard, without, Source);
    }

    public static async Task MachineUninstall(Action<string> log)
    {
        if (Elevation.IsElevated) await MachineSetup.Uninstall(log, Environment.ProcessId);
        else await Elevation.Run(log, "--machine-uninstall");
    }

    // appFile: the tray's executable to put in place, where it is not this process's own.
    public static async Task MachineComponent(Action<string> log, string component, bool installed, string? appFile = null)
    {
        if (Elevation.IsElevated)
        {
            var (sid, name) = Elevation.CurrentUser;
            await MachineSetup.SetComponent(log, Enum.Parse<Component>(component), installed, sid, name, Source, appFile, Environment.ProcessId);
        }
        else await Elevation.Run(log, "--machine-component", [component, installed ? "add" : "remove", .. appFile is null ? Array.Empty<string>() : ["--app-file", appFile], .. SourceArgs]);
    }

    public static async Task UpdateCli(Action<string> log, Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            log($"Downloading the dockyard command {release.Tag}");
            var cli = await Updates.DownloadReleaseAsset(Updates.CliAssetPattern, dir, isPattern: true, tag: release.Tag);
            if (Elevation.IsElevated) await MachineSetup.Update(log, Component.DockyardCli, cli, Environment.ProcessId, Source);
            else await Elevation.Run(log, "--machine-update", Component.DockyardCli.ToString(), "--app-file", cli);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public static async Task UpdateTray(Action<string> log, Release release)
    {
        log($"Downloading the Dockyard tray {release.Tag}");
        var exe = await Updates.DownloadTray(release);
        try
        {
            if (Elevation.IsElevated) await MachineSetup.Update(log, Component.Tray, exe, Environment.ProcessId, Source);
            else await Elevation.Run(log, "--machine-update", Component.Tray.ToString(), "--app-file", exe);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(exe)!, recursive: true);
        }
    }

    public static Task MachineUpdate(Action<string> log, IReadOnlyCollection<Component> components) =>
        Elevation.Run(log, "--machine-update", [string.Join(',', components), .. SourceArgs]);

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
    // setup's administrator approval is the prompt that explains itself.
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
