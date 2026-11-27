using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

sealed class CtxSettings : CommandSettings
{
    [CommandArgument(0, "[context]")]
    [Description("The kube context; asked for when not given")]
    public string? Context { get; init; }
}

sealed class CtxCommand(CliEnvironment env) : Command<CtxSettings>
{
    public override int Execute(CommandContext context, CtxSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        var name = s.Context;
        if (name is null)
        {
            var contexts = setup.KubeContexts();
            if (contexts.Names.Count == 0) throw new UsageException("There are no contexts in your kubeconfig");
            var ordered = contexts.Names.OrderBy(n => n != contexts.Current).ToList();
            if (!env.HasTerminal)
            {
                foreach (var n in ordered) env.Console.WriteLine(n);
                env.Error.WriteLine("Name one of these contexts: dockyard ctx <context>");
                return Dockyard.Setup.DockyardSetup.WrongArguments;
            }
            name = env.Console.Prompt(new SelectionPrompt<string>().Title("Open a shell on").AddChoices(ordered));
        }
        var path = setup.WriteContextKubeconfig(name);
        try
        {
            env.Shell.Run(path, name);
            return 0;
        }
        finally { File.Delete(path); }
    }
}
