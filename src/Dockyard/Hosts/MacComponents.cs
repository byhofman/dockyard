using System.Diagnostics;
using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// What Dockyard puts in place on a Mac apart from the VM: the docker CLI, its plugins, kind, the dashboard and the app itself.
[SupportedOSPlatform("macos")]
sealed class MacComponents(ICommandRunner runner)
{
    public static bool HasDashboard => MacLayout.HasDashboardIn(MacLayout.AppBase);

    public async Task<Version?> Installed(string component) => component switch
    {
        "LinuxEngine" => File.Exists(MacLayout.RootRelease) ? MacLayout.DockerVersion(await File.ReadAllTextAsync(MacLayout.RootRelease)) : null,
        "Cli" => File.Exists(MacLayout.DockerCli) ? Updates.ParseVersion((await runner.Run(MacLayout.DockerCli, "--version")).Output) : null,
        "Compose" => await Plugin("docker-compose", "version", "--short"),
        "Buildx" => await Plugin("docker-buildx", "version"),
        Kind.Id => File.Exists(MacLayout.KindCli) ? Updates.ParseVersion((await runner.Run(MacLayout.KindCli, "version")).Output) : null,
        "App" => InstalledAppVersion(),
        ComponentRules.DashboardId => await InstalledDashboardVersion(),
        _ => null,
    };

    // Asking the old binary would open its window, since it has no --version: it came with the app's version.
    async Task<Version?> InstalledDashboardVersion()
    {
        var current = Path.Combine(MacLayout.AppBase, MacLayout.DashboardNames[0]);
        if (File.Exists(current)) return Updates.ParseVersion((await runner.Run(current, "--version")).Output);
        return HasDashboard ? InstalledAppVersion() : null;
    }

    // The wizard runs from a download and can be newer than what is installed.
    static Version? InstalledAppVersion()
    {
        var plist = Path.Combine(MacLayout.AppBase, "..", "Info.plist");
        return File.Exists(plist) ? MacLayout.BundleVersion(File.ReadAllText(plist)) : Updates.ParseVersion(Paths.AppVersion);
    }

    public static async Task<Release?> Latest(string component) => component switch
    {
        "LinuxEngine" => File.Exists(Path.Combine(MacLayout.Assets, "dockyard-release")) && MacLayout.DockerVersion(
            await File.ReadAllTextAsync(Path.Combine(MacLayout.Assets, "dockyard-release"))) is { } bundled
            ? new Release(bundled, bundled.ToString(), null) : null,
        "Cli" => MacLayout.Cli,
        "Compose" => MacLayout.Compose,
        "Buildx" => MacLayout.Buildx,
        Kind.Id => Kind.Release,
        "App" => await Updates.LatestApp(MacLayout.ZipAsset(!HasDashboard)),
        ComponentRules.DashboardId => await Updates.LatestApp(MacLayout.ZipAsset(engineOnly: false)),
        _ => null,
    };

    // From this bundle when it has the dashboard (the wizard), otherwise from the release of this version, or the one named.
    public async Task AddDashboard(Action<string> log, string? tag = null)
    {
        string? downloaded = null;
        try
        {
            var source = AppContext.BaseDirectory;
            if (tag is not null || !MacLayout.HasDashboardIn(source))
            {
                log("Downloading the dashboard");
                downloaded = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
                var zip = await Updates.DownloadReleaseAsset(MacLayout.ZipAsset(engineOnly: false), downloaded, isPattern: true, tag);
                await runner.Check("/usr/bin/ditto", "-x", "-k", zip, downloaded);
                source = Path.Combine(downloaded, "Dockyard.app", "Contents", "MacOS");
            }
            await runner.Run("/usr/bin/pkill", "-f", Path.Combine(MacLayout.AppBase, "Dockyard[. ]Dashboard"));
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
            if (downloaded is not null) Directory.Delete(downloaded, recursive: true);
        }
    }

    public async Task RemoveDashboard(Action<string> log)
    {
        await runner.Run("/usr/bin/pkill", "-f", Path.Combine(MacLayout.AppBase, "Dockyard[. ]Dashboard"));
        foreach (var file in MacLayout.DashboardFiles(MacLayout.AppBase)) File.Delete(file);
        await SealBundle();
        log("Removed the dashboard");
    }

    // Only the bundle's own seal changes: signing it without --deep leaves dockyard-vm and its entitlements alone.
    async Task SealBundle()
    {
        if (MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar) is { } bundle)
            await runner.Run("/usr/bin/codesign", "--force", "--sign", "-", bundle);
    }

    // Downloads the release zip (no quarantine attribute, so Gatekeeper does not block it) and swaps the
    // app bundle once this process has exited.
    public async Task SelfUpdate(Action<string> log, bool engineOnly, string? tag)
    {
        var bundle = MacLayout.Bundle(MacLayout.AppBase + Path.DirectorySeparatorChar)
            ?? throw new InvalidOperationException("Dockyard is not running from Dockyard.app; run install-mac.sh again");
        var dir = Path.Combine(Path.GetTempPath(), $"dockyard-update-{Guid.NewGuid():N}");
        var zip = await Updates.DownloadReleaseAsset(MacLayout.ZipAsset(engineOnly), dir, isPattern: true, tag);
        await runner.Check("/usr/bin/ditto", "-x", "-k", zip, dir);
        var script = Path.Combine(dir, "swap.sh");
        await File.WriteAllTextAsync(script, MacLayout.SwapScript(Environment.ProcessId, Path.Combine(dir, "Dockyard.app"), bundle));
        Process.Start(new ProcessStartInfo("/bin/sh", [script]) { UseShellExecute = false })?.Dispose();
        log($"Replacing {bundle} and restarting");
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

    public static async Task InstallKind(Action<string> log)
    {
        Directory.CreateDirectory(MacLayout.BinDir);
        await Kind.Download(MacLayout.KindCli);
        log($"kind {Kind.Tag} in {MacLayout.BinDir}");
    }

    public static void RemoveKind(Action<string> log)
    {
        if (!File.Exists(MacLayout.KindCli)) return;
        File.Delete(MacLayout.KindCli);
        log("Removed kind");
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

    async Task<Version?> Plugin(string name, params string[] args)
    {
        var path = Path.Combine(MacLayout.PluginDir, name);
        return File.Exists(path) ? Updates.ParseVersion((await runner.Run(path, args)).Output) : null;
    }
}
