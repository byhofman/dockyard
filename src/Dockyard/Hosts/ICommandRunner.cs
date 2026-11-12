namespace Dockyard.Hosts;

interface ICommandRunner
{
    Task<Core.ShellResult> Run(string file, params string[] args);
    System.Diagnostics.Process Spawn(string file, params string[] args);
}
