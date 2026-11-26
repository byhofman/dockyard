using System.Reflection;
using Dockyard.Setup;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

static class Commands
{
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static int Main(string[] args)
    {
        if (DockyardSetup.IsElevatedWorker(args)) return DockyardSetup.RunElevatedWorker(args);
        return Execute(args, CliEnvironment.ForThisMachine());
    }

    public static int Execute(IReadOnlyList<string> args, CliEnvironment env)
    {
        try
        {
            return CliApp.Create(env).Run(args);
        }
        catch (Exception e) when (e is UsageException or CommandAppException or ArgumentException)
        {
            env.Error.WriteLine(e.Message);
            env.Error.WriteLine("dockyard --help lists the commands.");
            return DockyardSetup.WrongArguments;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            env.Error.WriteLine(e.Message);
            return DockyardSetup.Failed;
        }
    }
}
