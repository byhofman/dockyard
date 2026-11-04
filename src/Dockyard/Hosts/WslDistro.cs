using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// The dedicated Alpine distro that runs the Linux engine on Windows.
[SupportedOSPlatform("windows")]
sealed class WslDistro(ICommandRunner runner, string name = WslDistro.DefaultName)
{
    public const string DefaultName = "dockyard";
    public const string InitPath = "/usr/local/sbin/dockyard-init";
    public const string UpdatePath = "/usr/local/sbin/dockyard-update";

    public string Name { get; } = name;
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dockyard", "wsl");

    public static async Task<string[]> List(ICommandRunner runner)
    {
        var r = await runner.Run("wsl.exe", "-l", "-q");
        return r.ExitCode != 0 ? [] : r.Output.Split('\n').Select(l => l.Trim().Trim('\0')).Where(l => l.Length > 0).ToArray();
    }

    public async Task<bool> Exists() => (await List(runner)).Contains(Name, StringComparer.OrdinalIgnoreCase);

    public async Task Import(string tarball, string? directory = null)
    {
        var dir = directory ?? InstallDir;
        Directory.CreateDirectory(dir);
        await runner.Check("wsl.exe", "--import", Name, dir, tarball, "--version", "2");
    }

    public string[] KeepAliveArgs(string? tcp = null) => tcp is null
        ? ["-d", Name, "-u", "root", "--", InitPath]
        : ["-d", Name, "-u", "root", "--", "env", $"DOCKYARD_TCP={tcp}", InitPath];

    public Task<ShellResult> StopEngine() => runner.Run("wsl.exe", "-d", Name, "-u", "root", "--", "sh", "-c",
        "pid=$(pidof dockerd) || exit 0; kill -TERM $pid; i=0; while pidof dockerd >/dev/null && [ $i -lt 150 ]; do sleep 0.2; i=$((i+1)); done; ! pidof dockerd >/dev/null");

    public async Task<Version?> DockerVersion()
    {
        var r = await runner.Run("wsl.exe", "-d", Name, "-u", "root", "--", "cat", "/etc/dockyard-release");
        if (r.ExitCode != 0) return null;
        var line = r.Output.Split('\n').FirstOrDefault(l => l.StartsWith("DOCKER_VERSION=", StringComparison.Ordinal));
        return line is null ? null : Version.TryParse(line["DOCKER_VERSION=".Length..].Trim(), out var v) ? v : null;
    }

    public async Task<string> ToLinuxPath(string windowsPath) =>
        (await runner.Check("wsl.exe", "-d", Name, "-u", "root", "--", "wslpath", "-a", windowsPath.Replace('\\', '/'))).Trim();

    public async Task<string> Update(string dockerTgz, string? pluginsDir)
    {
        List<string> args = ["-d", Name, "-u", "root", "--", UpdatePath, await ToLinuxPath(dockerTgz)];
        if (pluginsDir is not null) args.Add(await ToLinuxPath(pluginsDir));
        return (await runner.Check("wsl.exe", [.. args])).Trim();
    }

    // The Docker CE setup of earlier Dockyard versions in the user's own distro: stop it so nothing else listens on 2375.
    public static async Task<bool> DisableLegacyEngine(ICommandRunner runner, string distro)
    {
        if (!(await List(runner)).Contains(distro, StringComparer.OrdinalIgnoreCase)) return false;
        await runner.Run("wsl.exe", "-d", distro, "-u", "root", "--", "sh", "-c",
            "systemctl stop docker.service docker.socket containerd.service 2>/dev/null; systemctl disable docker.socket 2>/dev/null; true");
        return true;
    }
}
