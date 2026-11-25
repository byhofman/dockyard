using System.Reflection;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

static class Commands
{
    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    static readonly string[] OwnOptions = ["-h", "--help", "-?", "-v", "--version"];

    // Without a command dockyard opens the Toolbox; with the installer's options (--silent, --with, …) it installs.
    public static bool IsHandOver(IReadOnlyList<string> args) =>
        args.Count == 0 || (args[0].StartsWith('-') && !OwnOptions.Contains(args[0]));

    public static int Main(string[] args)
    {
        try
        {
            return IsHandOver(args) ? Toolbox.HandOver(args) : CliApp.Create(args).Run(args);
        }
        catch (Exception e) when (e is UsageException or CommandAppException)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine("dockyard --help lists the commands.");
            return 2;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }
}
