namespace Dockyard.Hosts;

sealed class ProcessRunner : ICommandRunner
{
    public static readonly ProcessRunner Instance = new();
    public Task<Core.ShellResult> Run(string file, params string[] args) => Core.Shell.Run(file, args);
    public Task<Core.ShellResult> RunWithin(TimeSpan limit, string file, params string[] args) => Core.Shell.Run(file, args, limit);
    public System.Diagnostics.Process Spawn(string file, params string[] args) => Core.Shell.Spawn(file, args);
}
