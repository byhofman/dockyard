using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Dockyard.Hosts;
using Microsoft.Win32;

namespace Dockyard.Core;

// Runs as the signed-in user: the WSL distro, docker contexts and autostart are per user.
[SupportedOSPlatform("windows")]
static partial class UserSetup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    // Windows 11 22H2; older builds ignore the setting and warn at every WSL start.
    const int MirroredNetworkingBuild = 22621;

    public static async Task Install(Action<string> log, string? image = null, IReadOnlyCollection<string>? without = null)
    {
        MigrateLegacySettings(log);
        var withLinux = without?.Contains(nameof(Component.LinuxEngine)) != true;
        if (withLinux)
        {
            await SetupDistro(log, image);
            await ApplyGuest(new WslDistro(ProcessRunner.Instance), log);
        }
        await MigrateLegacyEngine(log);
        await CreateContexts(log, without);
        SetMirroredNetworking(log);

        File.WriteAllText(Path.Combine(Paths.UserProfile, ".testcontainers.properties"), $"docker.host={Paths.LinuxHost}\n");
        log("Testcontainers pinned to the Linux engine");

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(Paths.AppName, $"\"{Paths.AppExe}\"");
        log($"{Paths.AppName} autostarts at logon");
    }

    public static async Task ApplyGuest(WslDistro distro, Action<string> log)
    {
        await using var bundle = typeof(UserSetup).Assembly.GetManifestResourceStream("guest.tar.gz");
        if (bundle is not null) await distro.ApplyGuest(bundle, log);
    }

    // Silent setups log to a file: after a restart there is no console to report to.
    public static void ResumeAfterReboot(bool silent, Action<string> log)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey);
        key.SetValue(Paths.AppName, $"\"{Paths.AppExe}\" {(silent ? "--silent" : "--setup")} --resume");
        log("Setup carries on by itself after the restart");
    }

    static void SetMirroredNetworking(Action<string> log)
    {
        if (Environment.OSVersion.Version.Build < MirroredNetworkingBuild) return;
        // Seen on Windows Server 2025: in mirrored mode WSL comes up with no network interfaces, and 127.0.0.1:2375 no longer
        // reaches the engine from Windows.
        using (var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            if (version?.GetValue("InstallationType") as string != "Client") return;
        var existing = File.Exists(Paths.WslConfigFile) ? File.ReadAllText(Paths.WslConfigFile) : null;
        if (WslConfig.WithMirroredNetworking(existing) is not { } updated) return;
        File.WriteAllText(Paths.WslConfigFile, updated);
        log("WSL uses mirrored networking from its next start (~/.wslconfig)");
    }

    public static async Task Uninstall(Action<string> log, bool removeData = false)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            key?.DeleteValue(Paths.AppName, throwOnMissingValue: false);
            key?.DeleteValue(Paths.LegacyName, throwOnMissingValue: false);
        }

        if (File.Exists(Paths.DockerExe))
        {
            await Shell.Run(Paths.DockerExe, "context", "use", "default");
            await Shell.Run(Paths.DockerExe, "context", "rm", "-f", "linux", "win");
            log("Removed docker contexts");
        }
        var tc = Path.Combine(Paths.UserProfile, ".testcontainers.properties");
        if (File.Exists(tc) && File.ReadAllText(tc).Trim() == $"docker.host={Paths.LinuxHost}") File.Delete(tc);

        if (File.Exists(Paths.WslConfigFile) && WslConfig.WithoutDockyardLine(File.ReadAllText(Paths.WslConfigFile)) is { } restored)
            File.WriteAllText(Paths.WslConfigFile, restored);

        var distro = new WslDistro(ProcessRunner.Instance);
        if (await distro.Exists())
        {
            await distro.StopEngine();
            if (removeData)
            {
                await distro.Unregister();
                log($"Removed the '{distro.Name}' WSL distro with its Linux images and containers");
            }
            else log($"The '{distro.Name}' WSL distro with your Linux images and containers was kept");
        }
        foreach (var dir in new[] { Paths.SettingsDir, Paths.LegacySettingsDir })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // Prefers a dockyard-linux-*.tar.gz next to the installer, then the matching release asset.
    public static string? FindLocalImage(string? explicitPath)
    {
        if (explicitPath is not null) return File.Exists(explicitPath) ? explicitPath : throw new FileNotFoundException("Image not found", explicitPath);
        var dir = Path.GetDirectoryName(Paths.Self)!;
        return Directory.EnumerateFiles(dir, "dockyard-linux-*-x86_64.tar.gz")
            .Where(f => ImageName().IsMatch(Path.GetFileName(f)))
            .OrderByDescending(f => Updates.ParseVersion(Path.GetFileName(f)))
            .FirstOrDefault();
    }

    static async Task SetupDistro(Action<string> log, string? image)
    {
        var distro = new WslDistro(ProcessRunner.Instance);
        if (await distro.Exists())
        {
            log($"WSL distro '{distro.Name}' already exists (Docker {await distro.DockerVersion()})");
            return;
        }

        var tarball = FindLocalImage(image);
        string? downloaded = null;
        if (tarball is null)
        {
            log("Downloading the Linux engine image");
            tarball = downloaded = await Updates.DownloadImage(Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}"));
        }
        try
        {
            log($"Importing WSL distro '{distro.Name}' into {WslDistro.InstallDir}");
            await distro.Import(tarball);
            log($"Linux engine: Docker {await distro.DockerVersion()}");
        }
        finally
        {
            if (downloaded is not null) Directory.Delete(Path.GetDirectoryName(downloaded)!, recursive: true);
        }
    }

    static async Task MigrateLegacyEngine(Action<string> log)
    {
        var settings = Settings.Load();
        if (settings.Distro is not { Length: > 0 } old || string.Equals(old, WslDistro.DefaultName, StringComparison.OrdinalIgnoreCase)) return;

        if (await WslDistro.DisableLegacyEngine(ProcessRunner.Instance, old))
        {
            log($"Stopped and disabled Docker CE in '{old}' (packages, images and containers are kept there).");
            log($"To copy an image: wsl -d {old} -u root -- docker save <image> | docker --context linux load");
        }
        settings.Distro = null;
        settings.Save();
    }

    static void MigrateLegacySettings(Action<string> log)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            key?.DeleteValue(Paths.LegacyName, throwOnMissingValue: false);

        var legacy = Path.Combine(Paths.LegacySettingsDir, "settings.json");
        if (File.Exists(legacy) && !File.Exists(Paths.SettingsFile))
        {
            Directory.CreateDirectory(Paths.SettingsDir);
            File.Copy(legacy, Paths.SettingsFile);
            log($"Migrated settings from {Paths.LegacySettingsDir}");
        }
        if (Directory.Exists(Paths.LegacySettingsDir)) Directory.Delete(Paths.LegacySettingsDir, recursive: true);
    }

    static async Task CreateContexts(Action<string> log, IReadOnlyCollection<string>? without = null)
    {
        var withLinux = without?.Contains(nameof(Component.LinuxEngine)) != true;
        var withWindows = without?.Contains(nameof(Component.Engine)) != true;
        if (withLinux) await AddContext("linux", Paths.LinuxHost);
        else await RemoveContext("linux", "win", log);
        if (withWindows) await AddContext("win", Paths.WindowsHost);
        else await RemoveContext("win", "linux", log);
        var current = (await Shell.Check(Paths.DockerExe, "context", "show")).Trim();
        if (current is not ("linux" or "win")) await Shell.Check(Paths.DockerExe, "context", "use", withLinux ? "linux" : "win");
        log(withLinux && withWindows ? "Docker contexts: linux (default), win" : $"Docker context: {(withLinux ? "linux" : "win")}");
    }

    public static async Task AddContext(string name, string host)
    {
        var exists = (await Shell.Run(Paths.DockerExe, "context", "inspect", name)).ExitCode == 0;
        await Shell.Check(Paths.DockerExe, "context", exists ? "update" : "create", name, "--docker", $"host={host}");
    }

    // The docker command must not be left pointing at an engine that is gone.
    public static async Task RemoveContext(string name, string fallback, Action<string> log)
    {
        if (!File.Exists(Paths.DockerExe)) return;
        if ((await Shell.Run(Paths.DockerExe, "context", "show")).Output.Trim() == name)
        {
            await Shell.Run(Paths.DockerExe, "context", "use", fallback);
            log($"The docker context is now {fallback}");
        }
        await Shell.Run(Paths.DockerExe, "context", "rm", "-f", name);
    }

    public static async Task EnableLinuxEngine(Action<string> log)
    {
        await SetupDistro(log, null);
        await ApplyGuest(new WslDistro(ProcessRunner.Instance), log);
        await AddContext("linux", Paths.LinuxHost);
    }

    public static async Task DisableLinuxEngine(Action<string> log, bool removeData)
    {
        await RemoveContext("linux", "win", log);
        var distro = new WslDistro(ProcessRunner.Instance);
        if (!await distro.Exists()) return;
        if (removeData)
        {
            await distro.Unregister();
            log($"Removed the '{distro.Name}' WSL distro with its Linux images and containers");
        }
        else log($"The '{distro.Name}' WSL distro is kept with your Linux images and containers; adding the Linux engine back finds them");
    }

    [GeneratedRegex(@"^dockyard-linux-\d+\.\d+\.\d+-x86_64\.tar\.gz$")]
    private static partial Regex ImageName();
}
