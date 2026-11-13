using System.Reflection;
using System.Runtime.Versioning;
using Dockyard.Core;

namespace Dockyard.Hosts;

// How Dockyard gets root on a Linux host, for a command or for a step of setup-linux.sh.
[SupportedOSPlatform("linux")]
sealed class LinuxRoot(ICommandRunner runner)
{
    public async Task<ShellResult?> TryRun(string file, params string[] args)
    {
        try { return await runner.Run(file, args); }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public async Task<ShellResult> RunElevated(string[] command)
    {
        var prefix = await ElevationPrefix() ?? throw new InvalidOperationException(
            "This step needs root, and there is no way to ask here: no passwordless sudo, desktop or terminal. Run it as root or from a terminal.");
        string[] full = [.. prefix, .. command];
        return await runner.Run(full[0], full[1..]);
    }

    async Task<string[]?> ElevationPrefix() => LinuxElevation.Prefix(
        isRoot: Environment.UserName == "root",
        passwordlessSudo: (await TryRun("sudo", "-n", "true"))?.ExitCode == 0,
        hasDisplay: LinuxLayout.HasDisplay(Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
        hasPkexec: OnPath("pkexec"),
        hasSudo: OnPath("sudo"),
        hasTerminal: HasTerminal());

    static bool OnPath(string file) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => dir.Length > 0 && File.Exists(Path.Combine(dir, file)));

    static bool HasTerminal()
    {
        try
        {
            using var tty = File.Open("/dev/tty", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task RunSetup(Action<string> log, params string[] args)
    {
        var script = Path.Combine(Path.GetTempPath(), $"dockyard-setup-{Guid.NewGuid():N}.sh");
        await using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup-linux.sh")!)
        using (var reader = new StreamReader(stream))
            await File.WriteAllTextAsync(script, (await reader.ReadToEndAsync()).Replace("\r\n", "\n"));
        try
        {
            log("Administrator step");
            var r = await RunElevated(["sh", script, .. args]);
            foreach (var line in Lines(r.Output + r.Error)) log("  " + line);
            if (r.ExitCode == 126 || r.ExitCode == 127) throw new InvalidOperationException("Administrator approval was declined");
            if (r.ExitCode != 0) throw new InvalidOperationException($"Setup failed ({r.ExitCode})");
        }
        finally
        {
            File.Delete(script);
        }
    }

    public static IEnumerable<string> Lines(string text) => text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0);
}
