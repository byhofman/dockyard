namespace Dockyard.Hosts;

interface ICommandRunner
{
    Task<Core.ShellResult> Run(string file, params string[] args);
    /// <summary>As Run, but a process still running at the limit is killed and answers exit code -1.</summary>
    Task<Core.ShellResult> RunWithin(TimeSpan limit, string file, params string[] args) => Run(file, args);
    System.Diagnostics.Process Spawn(string file, params string[] args);
}
