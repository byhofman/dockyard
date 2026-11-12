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
    public const string AgentPath = "/usr/local/sbin/dockyard-agent";
    public const string GuestMarker = "/etc/dockyard-guest";

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

    public Task Unregister() => runner.Check("wsl.exe", "--unregister", Name);

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

    // The image is only imported once, so everything newer than the first install's image reaches the distro from here.
    // A bundle that is already there is left alone, by its hash because development builds all have the same version.
    public async Task ApplyGuest(Stream bundle, Action<string> log)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dockyard-guest-{Guid.NewGuid():N}.tar.gz");
        await using (var file = File.Create(temp)) await bundle.CopyToAsync(file);
        try
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(temp)));
            var current = await runner.Run("wsl.exe", "-d", Name, "-u", "root", "--", "cat", GuestMarker);
            if (current.ExitCode == 0 && current.Output.Trim() == hash) return;
            var path = await ToLinuxPath(temp);
            await runner.Check("wsl.exe", "-d", Name, "-u", "root", "--", "sh", "-c",
                $"mkdir -p /etc/docker && tar -xzf {Shell.ShQuote(path)} -C / && chmod 0755 {InitPath} {UpdatePath} {AgentPath} && echo {hash} > {GuestMarker}");
            log("Linux engine: Dockyard's part is up to date");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    // Asked the way the agent reaches Windows (WslHost.Address): by loopback when networking is mirrored, else by the gateway.
    // Separate commands, because wsl.exe hands its command line to a shell first, which would expand any $ in it.
    public async Task<bool> ReachesHostPort(int port)
    {
        var mirrored = (await runner.Run("wsl.exe", "-d", Name, "-u", "root", "--", "test", "-d", "/sys/class/net/loopback0")).ExitCode == 0;
        var host = mirrored ? "127.0.0.1" : DefaultGateway((await runner.Run("wsl.exe", "-d", Name, "-u", "root", "--", "ip", "route", "show", "default")).Output);
        return host is not null && (await runner.Run("wsl.exe", "-d", Name, "-u", "root", "--", "nc", "-z", "-w", "3", host, port.ToString())).ExitCode == 0;
    }

    // Output of `ip route show default`: "default via 172.20.0.1 dev eth0 ...".
    public static string? DefaultGateway(string route)
    {
        var words = route.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var via = Array.IndexOf(words, "via");
        return via >= 0 && via + 1 < words.Length ? words[via + 1] : null;
    }

    // Readable by root alone with secret, which is everyone who could run the engine anyway.
    public async Task WriteFile(string path, string content, bool secret = false)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"dockyard-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(temp, content);
        try
        {
            var mode = secret ? "0600" : "0644";
            await runner.Check("wsl.exe", "-d", Name, "-u", "root", "--", "sh", "-c", $"mkdir -p \"$(dirname {Shell.ShQuote(path)})\" && install -m {mode} {Shell.ShQuote(await ToLinuxPath(temp))} {Shell.ShQuote(path)}");
        }
        finally
        {
            File.Delete(temp);
        }
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
