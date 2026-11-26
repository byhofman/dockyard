namespace Dockyard.Hosts;

static class RunnerExtensions
{
    public static async Task<string> Check(this ICommandRunner runner, string file, params string[] args)
    {
        var r = await runner.Run(file, args);
        if (r.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(file)} {string.Join(' ', args)} failed ({r.ExitCode}): {(r.Error + r.Output).Trim()}");
        return r.Output;
    }

    // Every process whose command line matches, except this one, which may be the very app being replaced.
    public static async Task StopMatching(this ICommandRunner runner, string pattern)
    {
        Core.ShellResult found;
        try { found = await runner.Run("pgrep", "-f", pattern); }
        catch (System.ComponentModel.Win32Exception) { return; }
        foreach (var line in found.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(line, out var pid) && pid != Environment.ProcessId) await runner.Run("kill", pid.ToString());
    }
}
