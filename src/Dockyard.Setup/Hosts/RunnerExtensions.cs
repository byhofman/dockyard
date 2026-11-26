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
}
