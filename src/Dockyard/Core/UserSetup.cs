using System.Reflection;
using Microsoft.Win32;

namespace Dockyard.Core;

// Runs as the signed-in user: WSL distros, docker contexts and autostart are per user.
static class UserSetup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task<string[]> ListDistros()
    {
        var r = await Shell.Run("wsl.exe", "-l", "-q");
        return r.ExitCode != 0 ? [] : r.Output.Split('\n').Select(l => l.Trim().Trim('\0')).Where(l => l.Length > 0).ToArray();
    }

    public static async Task Install(Action<string> log, string distro)
    {
        MigrateLegacy(log);
        await SetupWsl(log, distro);
        await CreateContexts(log);

        File.WriteAllText(Path.Combine(Paths.UserProfile, ".testcontainers.properties"), $"docker.host={Paths.LinuxHost}\n");
        log("Testcontainers pinned to the Linux engine");

        var settings = Settings.Load();
        settings.Distro = distro;
        settings.Save();

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            key.SetValue(Paths.AppName, $"\"{Paths.AppExe}\"");
        log($"{Paths.AppName} autostarts at logon");
    }

    public static async Task UpdateWsl(Action<string> log, string distro)
    {
        log($"Upgrading Docker CE in WSL '{distro}'");
        var r = await Shell.Run("wsl.exe", "-d", distro, "-u", "root", "--", "sh", "-c",
            "DEBIAN_FRONTEND=noninteractive apt-get install -y --only-upgrade docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin");
        foreach (var line in Lines(r.Output + r.Error).TakeLast(4)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"apt-get upgrade failed ({r.ExitCode})");
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

        var distro = Settings.Load().Distro;
        if ((await ListDistros()).Contains(distro))
            await Shell.Run("wsl.exe", "-d", distro, "-u", "root", "--", "systemctl", "stop", "docker.service", "docker.socket", "containerd.service");
        foreach (var dir in new[] { Paths.SettingsDir, Paths.LegacySettingsDir })
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        log($"Docker CE packages in WSL '{distro}' were left installed (apt remove docker-ce to drop them)");
    }

    static void MigrateLegacy(Action<string> log)
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

    static async Task SetupWsl(Action<string> log, string distro)
    {
        if (!(await ListDistros()).Contains(distro))
            throw new InvalidOperationException($"WSL distro '{distro}' not found. Install it, launch it once to create your user, then run setup again.");

        var user = (await Shell.Check("wsl.exe", "-d", distro, "--", "whoami")).Trim();

        // WSL keeps a handle on its --cd directory for a while after exiting, so only the file is removed.
        var dir = Path.GetTempPath();
        var script = $"dockyard-{Guid.NewGuid():N}.sh";
        await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-wsl-docker.sh")!)
        using (var reader = new StreamReader(stream))
            await File.WriteAllTextAsync(Path.Combine(dir, script), (await reader.ReadToEndAsync()).Replace("\r\n", "\n"));

        log($"Setting up Docker CE in WSL '{distro}' (user {user})");
        ShellResult r;
        try
        {
            r = await RunScript(distro, dir, script, user);
            if (r.ExitCode == 2)
            {
                log("Enabled systemd in the distro; restarting it");
                await Shell.Run("wsl.exe", "--terminate", distro);
                r = await RunScript(distro, dir, script, user);
            }
        }
        finally
        {
            try { File.Delete(Path.Combine(dir, script)); } catch (IOException) { }
        }
        foreach (var line in Lines(r.Output + r.Error)) log("  " + line);
        if (r.ExitCode != 0) throw new InvalidOperationException($"WSL setup failed ({r.ExitCode})");
    }

    static Task<ShellResult> RunScript(string distro, string dir, string script, string user) =>
        Shell.Run("wsl.exe", "-d", distro, "-u", "root", "--cd", dir, "--", "bash", script, user);

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

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);
}
