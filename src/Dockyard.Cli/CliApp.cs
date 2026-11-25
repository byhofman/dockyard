using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

// The dockyard command tree. Cluster commands run here; engine, config, https and update act on what the Toolbox owns and
// go to it with the arguments as they were typed.
static class CliApp
{
    public static CommandApp Create(IReadOnlyList<string> args)
    {
        var app = new CommandApp();
        app.Configure(c =>
        {
            c.SetApplicationName("dockyard");
            c.SetApplicationVersion(Commands.Version);
            c.PropagateExceptions();
            c.Settings.Registrar.RegisterInstance(new RawArguments(args));
            c.AddBranch("cluster", b =>
            {
                b.SetDescription("kind clusters as the dashboard makes them: the context in your kubeconfig, the definition kept.");
                b.AddCommand<ClusterListCommand>("ls").WithDescription("The clusters, with their storage, ports and add-ons.");
                b.AddCommand<ClusterCreateCommand>("create").WithDescription("Makes a cluster and adds its context to your kubeconfig.")
                    .WithExample("cluster", "create", "dev", "--workers", "2", "--addons", "metrics-server,traefik", "--storage", "nfs");
                b.AddCommand<ClusterRecreateCommand>("recreate").WithDescription("Deletes it, with everything in it, and makes it again from its definition or the one given.");
                b.AddCommand<ClusterDeleteCommand>("delete").WithDescription("Deletes it and takes its context out of your kubeconfig.");
                b.AddCommand<ClusterUseCommand>("use").WithDescription("Makes its context the current one.");
                b.AddCommand<ClusterKubeconfigCommand>("kubeconfig").WithDescription("Prints its kubeconfig.");
                b.AddCommand<ClusterDefinitionCommand>("definition").WithDescription("Prints the kind config it was made from.");
            });
            c.AddBranch("engine", b =>
            {
                b.SetDescription("The engines: linux, and win on Windows.");
                b.AddCommand<ToolboxCommand>("ls").WithDescription("The engines and whether they run; * marks the docker context's.");
                b.AddCommand<EngineCommand>("start").WithDescription("Starts an engine.");
                b.AddCommand<EngineCommand>("stop").WithDescription("Stops an engine.");
                b.AddCommand<EngineCommand>("use").WithDescription("Makes it the docker context.");
            });
            c.AddBranch("config", b =>
            {
                b.SetDescription("An engine's daemon.json: what you set in it, without the keys Dockyard adds.");
                b.AddCommand<ConfigCommand>("show").WithDescription("Prints it.");
                b.AddCommand<ConfigFileCommand>("validate").WithDescription("Checks a file without applying it.");
                b.AddCommand<ConfigFileCommand>("apply").WithDescription("Checks it, restarts the engine if it runs, and puts the previous one back if it will not start.");
                b.AddCommand<ConfigEditCommand>("edit").WithDescription("Opens it in your editor (VISUAL or EDITOR) and applies it.");
            });
            c.AddCommand<HttpsCommand>("https").WithDescription("Trusted HTTPS for container domains, with Dockyard's own certificate authority: on or off.");
            c.AddCommand<UpdateCommand>("update").WithDescription("Brings the components up to date; --check only lists them.");
            c.AddCommand<VersionCommand>("version").WithDescription("Prints this command's version.");
        });
        return app;
    }
}

sealed record RawArguments(IReadOnlyList<string> Args);

sealed class NameSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("The cluster: lower case letters, digits, . and -")]
    public string Name { get; init; } = "";

    public override ValidationResult Validate() =>
        Kind.KindNames.IsValid(Name) ? ValidationResult.Success() : ValidationResult.Error("A cluster name is lower case letters, digits, . and -");
}

sealed class CreateSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("The cluster: lower case letters, digits, . and -")]
    public string Name { get; init; } = "";

    [CommandOption("--workers <n>")]
    [Description("Worker nodes besides the control plane, 0 to 10")]
    public int Workers { get; init; }

    [CommandOption("--addons <names>")]
    [Description("metrics-server, traefik, cert-manager (with Helm), comma separated")]
    public string? Addons { get; init; }

    [CommandOption("--storage <kind>")]
    [Description("Storage every node uses at once: none, host-folder or nfs (with Helm)")]
    public string? Storage { get; init; }

    [CommandOption("--no-load-balancer")]
    [Description("No load balancer for its LoadBalancer services")]
    public bool NoLoadBalancer { get; init; }

    [CommandOption("--ports <ports>")]
    [Description("Fixed ports on 127.0.0.1 for its load balancer; none when not given")]
    public string? Ports { get; init; }

    [CommandOption("--file <definition>")]
    [Description("kind's cluster config to make it from, or - for standard input")]
    public string? File { get; init; }

    public override ValidationResult Validate() =>
        Kind.KindNames.IsValid(Name) ? ValidationResult.Success() : ValidationResult.Error("A cluster name is lower case letters, digits, . and -");
}

sealed class RecreateSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    [CommandOption("--file <definition>")]
    [Description("kind's cluster config to make it from instead of its own, or - for standard input")]
    public string? File { get; init; }
}

static class Files
{
    public static string? Read(string? file) => file is null ? null : file == "-" ? Console.In.ReadToEnd() : System.IO.File.ReadAllText(file);
}

sealed class ClusterListCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        var clusters = await new Clusters(Console.WriteLine).List();
        if (clusters.Count == 0)
        {
            Console.WriteLine("No clusters");
            return 0;
        }
        var table = new Table().Border(TableBorder.None).AddColumn("NAME").AddColumn("DETAILS");
        foreach (var (name, details) in clusters) table.AddRow(Markup.Escape(name), Markup.Escape(details));
        AnsiConsole.Write(table);
        return 0;
    }
}

sealed class ClusterCreateCommand : AsyncCommand<CreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CreateSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Create(s.Name, s.Workers, Clusters.Addons(s.Addons), Clusters.Storage(s.Storage), !s.NoLoadBalancer, Clusters.Ports(s.Ports), Files.Read(s.File));
        return 0;
    }
}

sealed class ClusterRecreateCommand : AsyncCommand<RecreateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RecreateSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Recreate(Clusters.ValidName(s.Name), Files.Read(s.File));
        return 0;
    }
}

sealed class ClusterDeleteCommand : AsyncCommand<NameSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NameSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Delete(s.Name);
        return 0;
    }
}

sealed class ClusterUseCommand : AsyncCommand<NameSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NameSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Use(s.Name);
        return 0;
    }
}

sealed class ClusterKubeconfigCommand : AsyncCommand<NameSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NameSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Kubeconfig(s.Name);
        return 0;
    }
}

sealed class ClusterDefinitionCommand : Command<NameSettings>
{
    public override int Execute(CommandContext context, NameSettings s, CancellationToken cancellation)
    {
        new Clusters(Console.WriteLine).Definition(s.Name);
        return 0;
    }
}

// The arguments go to the Toolbox as they were typed: it checks them again, being the one that knows its engines.
class ToolboxCommand(RawArguments raw) : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellation) =>
        Toolbox.Run(raw.Args, Console.WriteLine, Console.Error.WriteLine);
}

sealed class EngineSettings : CommandSettings
{
    [CommandArgument(0, "<engine>")]
    [Description("linux, or win on Windows")]
    public string Engine { get; init; } = "";
}

sealed class EngineCommand(RawArguments raw) : Command<EngineSettings>
{
    public override int Execute(CommandContext context, EngineSettings s, CancellationToken cancellation) =>
        Toolbox.Run(raw.Args, Console.WriteLine, Console.Error.WriteLine);
}

class ConfigSettings : CommandSettings
{
    [CommandOption("--engine <engine>")]
    [Description("linux or win (Windows); the active engine without it")]
    public string? Engine { get; init; }
}

sealed class ConfigFileSettings : ConfigSettings
{
    [CommandOption("--file <file>")]
    [Description("A daemon.json, or - for standard input")]
    public string File { get; init; } = "";

    public override ValidationResult Validate() => File.Length > 0 ? ValidationResult.Success() : ValidationResult.Error("--file is required");
}

sealed class ConfigCommand(RawArguments raw) : Command<ConfigSettings>
{
    public override int Execute(CommandContext context, ConfigSettings s, CancellationToken cancellation) =>
        Toolbox.Run(raw.Args, Console.WriteLine, Console.Error.WriteLine);
}

sealed class ConfigFileCommand(RawArguments raw) : Command<ConfigFileSettings>
{
    public override int Execute(CommandContext context, ConfigFileSettings s, CancellationToken cancellation) =>
        Toolbox.Run(raw.Args, Console.WriteLine, Console.Error.WriteLine);
}

sealed class ConfigEditCommand : Command<ConfigSettings>
{
    public override int Execute(CommandContext context, ConfigSettings s, CancellationToken cancellation) =>
        ConfigEdit.Run(s.Engine is null ? [] : ["--engine", s.Engine]);
}

sealed class HttpsSettings : CommandSettings
{
    [CommandArgument(0, "<on|off>")]
    public string State { get; init; } = "";

    public override ValidationResult Validate() => State is "on" or "off" ? ValidationResult.Success() : ValidationResult.Error("https takes on or off");
}

sealed class HttpsCommand(RawArguments raw) : Command<HttpsSettings>
{
    public override int Execute(CommandContext context, HttpsSettings s, CancellationToken cancellation) =>
        Toolbox.Run(raw.Args, Console.WriteLine, Console.Error.WriteLine);
}

sealed class UpdateSettings : CommandSettings
{
    [CommandOption("--check")]
    [Description("Only list what has an update")]
    public bool Check { get; init; }
}

sealed class UpdateCommand(RawArguments raw) : Command<UpdateSettings>
{
    public override int Execute(CommandContext context, UpdateSettings s, CancellationToken cancellation) =>
        Toolbox.Run(raw.Args, Console.WriteLine, Console.Error.WriteLine);
}

sealed class VersionCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellation)
    {
        Console.WriteLine(Commands.Version);
        return 0;
    }
}
