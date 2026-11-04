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

    public static async Task Install(Action<string> log, string? image = null)
    {
        MigrateLegacySettings(log);
        await SetupDistro(log, image);
        await MigrateLegacyEngine(log);
        await CreateContexts(log);

        File.WriteAllText(Path.Combine(Paths.UserProfile, ".testcontainers.properties"), $"docker.host={Paths.LinuxHost}\n");
        log("Testcontainers pinned to the Linux engine");

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(Paths.AppName, $"\"{Paths.AppExe}\"");
        log($"{Paths.AppName} autostarts at logon");
    }

    public static async Task Uninstall(Action<string> log)
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

        var distro = new WslDistro(ProcessRunner.Instance);
        if (await distro.Exists())
        {
            await distro.StopEngine();
            log($"The '{distro.Name}' WSL distro with your Linux images and containers was kept; remove it with: wsl --unregister {distro.Name}");
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

    static async Task CreateContexts(Action<string> log)
    {
        foreach (var (name, host) in new[] { ("linux", Paths.LinuxHost), ("win", Paths.WindowsHost) })
        {
            var exists = (await Shell.Run(Paths.DockerExe, "context", "inspect", name)).ExitCode == 0;
            await Shell.Check(Paths.DockerExe, "context", exists ? "update" : "create", name, "--docker", $"host={host}");
        }
        var current = (await Shell.Check(Paths.DockerExe, "context", "show")).Trim();
        if (current is not ("linux" or "win")) await Shell.Check(Paths.DockerExe, "context", "use", "linux");
        log("Docker contexts: linux (default), win");
    }

    [GeneratedRegex(@"^dockyard-linux-\d+\.\d+\.\d+-x86_64\.tar\.gz$")]
    private static partial Regex ImageName();
}
