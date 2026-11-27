using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// What Dockyard puts in place on a Mac apart from the VM and the tools: the two apps (the CLI in ~/.dockyard/cli, the bundle
// with the tray and the dashboard), the engine's parts for a CLI without the bundle, and the docker CLI and its plugins
// where no Homebrew brings them.
[SupportedOSPlatform("macos")]
sealed class MacComponents(ICommandRunner runner)
{
    public static bool HasDashboard => MacLayout.HasDashboardIn(MacLayout.AppBase);

    public async Task<Version?> Installed(string component) => component switch
    {
        "LinuxEngine" => File.Exists(MacLayout.RootRelease) ? MacLayout.DockerVersion(await File.ReadAllTextAsync(MacLayout.RootRelease)) : null,
        nameof(Component.DockyardCli) => File.Exists(MacLayout.CliExe) ? Updates.ParseVersion((await runner.Run(MacLayout.CliExe, "--version")).Output) : null,
        ComponentRules.TrayId => File.Exists(MacLayout.LaunchAgent) ? InstalledBundleVersion() : null,
        ComponentRules.DashboardId => await InstalledDashboardVersion(),
        _ => null,
    };

    async Task<Version?> InstalledDashboardVersion() =>
        HasDashboard ? Updates.ParseVersion((await runner.Run(Path.Combine(MacLayout.AppBase, MacLayout.DashboardName), "--version")).Output) : null;

    // Setup runs from a download and can be newer than what is installed.
    static Version? InstalledBundleVersion()
    {
        var plist = Path.Combine(MacLayout.AppBase, "..", "Info.plist");
        return File.Exists(plist) ? MacLayout.BundleVersion(File.ReadAllText(plist)) : null;
    }

    public static bool HasBundle => MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle && Directory.Exists(bundle);

    public static async Task<Release?> Latest(string component) => component switch
    {
        "LinuxEngine" => File.Exists(Path.Combine(MacLayout.Assets, "dockyard-release")) && MacLayout.DockerVersion(
            await File.ReadAllTextAsync(Path.Combine(MacLayout.Assets, "dockyard-release"))) is { } bundled
            ? new Release(bundled, bundled.ToString(), null) : null,
        nameof(Component.DockyardCli) => await Updates.LatestComponentAsset(Updates.CliComponent, MacLayout.CliAsset),
        // The bundle holds the dashboard with the tray and the engine's parts, so its update is the whole bundle, with or
        // without the dashboard.
        ComponentRules.DashboardId or ComponentRules.TrayId => await Updates.LatestComponentAsset(Updates.DashboardComponent, MacLayout.BundleZip),
        _ => null,
    };

    // Puts Dockyard.app where it belongs: the bundle this process runs from (a disk image or a translocated copy), or the
    // dashboard's release. The login item and vm.json point at it, so it has to stay there. Nothing under ~/.dockyard is
    // touched.
    public async Task PlaceBundle(Action<string> log, string? fromBundle, string? tag = null)
    {
        string? downloaded = null;
        try
        {
            var source = fromBundle;
            if (source is null)
            {
                log("Downloading Dockyard.app");
                downloaded = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
                var zip = await Updates.DownloadComponentAsset(Updates.DashboardComponent, MacLayout.BundleZip, downloaded, tag);
                await runner.Check("/usr/bin/ditto", "-x", "-k", zip, downloaded);
                source = Path.Combine(downloaded, "Dockyard.app");
            }
            var target = MacLayout.BundleLocation;
            var incoming = target + ".new";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
            await runner.Check("/usr/bin/ditto", source, incoming);
            await runner.Run("/usr/bin/xattr", "-dr", "com.apple.quarantine", incoming);
            // Only that install's tray: its VM keeps running, and a Dockyard somewhere else is none of this setup's business.
            await runner.Run("/usr/bin/pkill", "-f", Path.Combine(target, "Contents", "MacOS", "Dockyard"));
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(incoming, target);
            log($"Installed {target}");
        }
        finally
        {
            if (downloaded is not null && Directory.Exists(downloaded)) Directory.Delete(downloaded, recursive: true);
        }
    }

    // Into the bundle when there is one, from this bundle when it has the dashboard (a setup run from it), otherwise from the
    // dashboard's release. Without a bundle, as after a CLI-only install, the dashboard is the bundle.
    public async Task AddDashboard(Action<string> log, string? tag = null)
    {
        if (!HasBundle)
        {
            await PlaceBundle(log, fromBundle: null, tag);
            RemoveEngineParts(log);
            return;
        }
        string? downloaded = null;
        try
        {
            var source = AppContext.BaseDirectory;
            if (tag is not null || !MacLayout.HasDashboardIn(source))
            {
                log("Downloading the dashboard");
                downloaded = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
                var zip = await Updates.DownloadComponentAsset(Updates.DashboardComponent, MacLayout.BundleZip, downloaded, tag);
                await runner.Check("/usr/bin/ditto", "-x", "-k", zip, downloaded);
                source = Path.Combine(downloaded, "Dockyard.app", "Contents", "MacOS");
            }
            await runner.StopMatching(Path.Combine(MacLayout.AppBase, MacLayout.DashboardName));
            foreach (var old in MacLayout.DashboardFiles(MacLayout.AppBase)) File.Delete(old);
            foreach (var file in MacLayout.DashboardFiles(source))
            {
                var target = Path.Combine(MacLayout.AppBase, Path.GetFileName(file));
                // A new inode: macOS kills a process whose signed file was overwritten in place.
                File.Delete(target);
                File.Copy(file, target);
            }
            await SealBundle();
            log("Added the dashboard");
        }
        finally
        {
            if (downloaded is not null && Directory.Exists(downloaded)) Directory.Delete(downloaded, recursive: true);
        }
    }

    // The bundle stays for the tray and the engine's parts, which the CLI goes on using.
    public async Task RemoveDashboard(Action<string> log)
    {
        await runner.StopMatching(Path.Combine(MacLayout.AppBase, MacLayout.DashboardName));
        foreach (var file in MacLayout.DashboardFiles(MacLayout.AppBase)) File.Delete(file);
        await SealBundle();
        log("Removed the dashboard");
    }

    // Only the bundle's own seal changes: signing it without --deep leaves dockyard-host and its entitlements alone.
    async Task SealBundle()
    {
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle)
            await runner.Run("/usr/bin/codesign", "--force", "--sign", "-", bundle);
    }

    // Downloads the dashboard's release zip (no quarantine attribute, so Gatekeeper does not block it) and swaps the
    // app bundle once this process has exited.
    public async Task UpdateBundle(Action<string> log, string? tag)
    {
        var bundle = MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } b && Directory.Exists(b) ? b
            : throw new InvalidOperationException("Dockyard.app is not installed; run dockyard setup again");
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-update-{Guid.NewGuid():N}");
        var zip = await Updates.DownloadComponentAsset(Updates.DashboardComponent, MacLayout.BundleZip, dir, tag);
        await runner.Check("/usr/bin/ditto", "-x", "-k", zip, dir);
        var script = Path.Combine(dir, "swap.sh");
        await File.WriteAllTextAsync(script, MacLayout.SwapScript(Environment.ProcessId, Path.Combine(dir, "Dockyard.app"), bundle));
        Process.Start(new ProcessStartInfo("/bin/sh", [script]) { UseShellExecute = false })?.Dispose();
        log($"Replacing {bundle} and restarting");
    }

    // The CLI that runs puts itself in place; anything else takes the CLI's release, or the file --assets names. Renamed into
    // place: a new inode, so a running copy goes on undisturbed.
    public async Task PlaceCli(Action<string> log, string? tag = null)
    {
        var self = ThisApp.Current == RunningApp.Cli && tag is null ? Paths.Self : null;
        if (self == MacLayout.CliExe) return;
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            var file = self ?? await Updates.DownloadComponentAsset(Updates.CliComponent, MacLayout.CliAsset, dir, tag);
            Directory.CreateDirectory(MacLayout.CliDir);
            PlaceExecutable(file, MacLayout.CliExe);
            log($"Installed {MacLayout.CliExe}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    public static void RemoveCli(Action<string> log)
    {
        if (!Directory.Exists(MacLayout.CliDir)) return;
        Directory.Delete(MacLayout.CliDir, recursive: true);
        log($"Removed {MacLayout.CliExe}");
    }

    // dockyard-host comes from the CLI's release, already signed with the virtualization entitlement, which works outside a
    // bundle too; the VM files from the engine's. The VM files are fetched only when missing or asked for.
    public async Task PlaceEngineParts(Action<string> log, string? cliTag = null, bool refreshVm = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        try
        {
            log("Downloading the engine's parts for the dockyard command");
            var host = await Updates.DownloadComponentAsset(Updates.CliComponent, MacLayout.HostAsset, dir, cliTag);
            Directory.CreateDirectory(MacLayout.OwnHostDir);
            PlaceExecutable(host, MacLayout.OwnHelper);
            log($"Installed {MacLayout.OwnHelper}");
            if (!refreshVm && File.Exists(Path.Combine(MacLayout.OwnAssets, "dockyard-release"))) return;
            var tarball = await Updates.DownloadComponentAsset(Updates.EngineComponent, Updates.VmAsset, dir);
            var incoming = MacLayout.OwnAssets + ".new";
            if (Directory.Exists(incoming)) Directory.Delete(incoming, recursive: true);
            Directory.CreateDirectory(incoming);
            await runner.Check("/usr/bin/tar", "-xzf", tarball, "-C", incoming);
            if (Directory.Exists(MacLayout.OwnAssets)) Directory.Delete(MacLayout.OwnAssets, recursive: true);
            Directory.Move(incoming, MacLayout.OwnAssets);
            log($"Installed the VM files in {MacLayout.OwnAssets}");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    // A bundle brings its own; the engine runs from it from the next start.
    public static void RemoveEngineParts(Action<string> log)
    {
        if (!MacLayout.HasBundleHelper || !Directory.Exists(MacLayout.OwnHostDir) && !Directory.Exists(MacLayout.OwnAssets)) return;
        foreach (var dir in new[] { MacLayout.OwnHostDir, MacLayout.OwnAssets })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        log("The engine runs from Dockyard.app from its next start");
    }

    static void PlaceExecutable(string file, string target)
    {
        var incoming = target + ".new";
        File.Copy(file, incoming, overwrite: true);
        File.SetUnixFileMode(incoming, (UnixFileMode)0b111_101_101);
        File.Move(incoming, target, overwrite: true);
    }

    public async Task InstallCli(Action<string> log, Release release)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var tgz = Path.Combine(dir, "docker.tgz");
            await Pinned.Download(release, tgz);
            await runner.Check("/usr/bin/tar", "-xzf", tgz, "-C", dir, "docker/docker");
            Directory.CreateDirectory(MacLayout.BinDir);
            File.Copy(Path.Combine(dir, "docker", "docker"), MacLayout.DockerCli, overwrite: true);
            File.SetUnixFileMode(MacLayout.DockerCli, (UnixFileMode)0b111_101_101);
            log($"docker CLI {release.Version} in {MacLayout.BinDir}");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static async Task InstallPlugin(Action<string> log, string name, Release release)
    {
        Directory.CreateDirectory(MacLayout.PluginDir);
        var target = Path.Combine(MacLayout.PluginDir, name);
        MacLayout.SetAsideLinkedPlugin(target, MacLayout.PluginLinksDir);
        await Pinned.Download(release, target);
        log($"{name} {release.Tag}");
    }

    public static void RemovePlugin(string name) => MacLayout.RemovePlugin(Path.Combine(MacLayout.PluginDir, name), MacLayout.PluginLinksDir);
}
