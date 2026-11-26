using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

// The dockyard command tree: parsing and rendering over Dockyard.Setup, and the cluster commands.
static class CliApp
{
    public static CommandApp Create(CliEnvironment env)
    {
        var app = new CommandApp();
        app.Configure(c =>
        {
            c.SetApplicationName("dockyard");
            c.SetApplicationVersion(Commands.Version);
            c.PropagateExceptions();
            c.Settings.Console = env.Console;
            c.Settings.Registrar.RegisterInstance(env);
            c.AddBranch("cluster", b =>
            {
                b.SetDescription("kind clusters as the dashboard makes them: the context in your kubeconfig, the definition kept.");
                b.AddCommand<ClusterListCommand>("ls").WithDescription("The clusters, with their storage, ports and add-ons.");
                b.AddCommand<ClusterCreateCommand>("create").WithDescription("Makes a cluster and adds its context to your kubeconfig.")
                    .WithExample("cluster", "create", "dev", "--workers", "2", "--addons", "metrics-server,traefik", "--storage", "nfs");
                b.AddCommand<ClusterRecreateCommand>("recreate").WithDescription("Deletes it, with everything in it, and makes it again from its definition or the one given.");
                b.AddCommand<ClusterUpgradeCommand>("upgrade").WithDescription("Makes it again on a newer Kubernetes: kind has no upgrade in place, so its workloads and node-local volumes go.")
                    .WithExample("cluster", "upgrade", "dev", "--kubernetes", "v1.37.0");
                b.AddCommand<ClusterDeleteCommand>("delete").WithDescription("Deletes it and takes its context out of your kubeconfig.");
        b.AddCommand<ClusterEditCommand>("edit").WithDescription("Changes the add-ons and NFS storage of a running cluster; the rest needs recreate.")
            .WithExample("cluster", "edit", "dev", "--addons", "metrics-server,traefik,headlamp", "--storage", "nfs");
        b.AddCommand<ClusterDashboardTokenCommand>("dashboard-token").WithDescription("Prints a token to sign in to Headlamp, valid for a day.");
                b.AddCommand<ClusterUseCommand>("use").WithDescription("Makes its context the current one.");
                b.AddCommand<ClusterKubeconfigCommand>("kubeconfig").WithDescription("Prints its kubeconfig.");
                b.AddCommand<ClusterDefinitionCommand>("definition").WithDescription("Prints the kind config it was made from.");
            });
            c.AddCommand<SetupCommand>("setup").WithDescription("Installs Dockyard, or brings an install up to date; asks which optional components, or takes --silent with --with and --without.")
                .WithExample("setup", "--silent", "--without", "tray,dashboard");
            c.AddBranch("component", b =>
            {
                b.SetDescription("The components: what Dockyard installs and where each comes from.");
                b.AddCommand<ComponentListCommand>("ls").WithDescription("The components, with their versions and source.");
                b.AddCommand<ComponentAddCommand>("add").WithDescription("Adds a component, by the flag ls shows.");
                b.AddCommand<ComponentRemoveCommand>("remove").WithDescription("Removes it; an engine keeps its images unless --remove-data.");
            });
            c.AddCommand<UpdateCommand>("update").WithDescription("Brings the components up to date; --check only lists them.");
            c.AddCommand<UninstallCommand>("uninstall").WithDescription("Removes Dockyard; images and containers stay unless --remove-data.");
            c.AddBranch("settings", b =>
            {
                b.SetDescription("Dockyard's settings.");
                b.AddCommand<SettingsListCommand>("ls").WithDescription("Every setting with its value.");
                b.AddCommand<SettingsGetCommand>("get").WithDescription("Prints a setting's value.");
                b.AddCommand<SettingsSetCommand>("set").WithDescription("Changes a setting; an engine's settings open in your editor.");
            });
            c.AddBranch("engine", b =>
            {
                b.SetDescription("The engines: linux, and win on Windows.");
                b.AddCommand<EngineListCommand>("ls").WithDescription("The engines and whether they run; * marks the docker context's.");
                b.AddCommand<EngineStartCommand>("start").WithDescription("Starts an engine.");
                b.AddCommand<EngineStopCommand>("stop").WithDescription("Stops an engine.");
                b.AddCommand<EngineUseCommand>("use").WithDescription("Makes it the docker context.");
            });
            c.AddBranch("config", b =>
            {
                b.SetDescription("An engine's daemon.json: what you set in it, without the keys Dockyard adds.");
                b.AddCommand<ConfigShowCommand>("show").WithDescription("Prints it.");
                b.AddCommand<ConfigValidateCommand>("validate").WithDescription("Checks a file without applying it.");
                b.AddCommand<ConfigApplyCommand>("apply").WithDescription("Checks it, restarts the engine if it runs, and puts the previous one back if it will not start.");
                b.AddCommand<ConfigEditCommand>("edit").WithDescription("Opens it in your editor (VISUAL or EDITOR) and applies it.");
            });
            c.AddCommand<HttpsCommand>("https").WithDescription("Trusted HTTPS for container domains, with Dockyard's own certificate authority: on or off.");
            c.AddCommand<ImportCommand>("import").WithDescription("Copies images, volumes and containers from Docker Desktop, which keeps them.");
            c.AddCommand<VersionCommand>("version").WithDescription("Prints this command's version.");
        });
        return app;
    }
}

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
    [Description("metrics-server, traefik, cert-manager, victoria-metrics, headlamp (with Helm), comma separated")]
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

    [CommandOption("--kubernetes <version>")]
    [Description("The Kubernetes version, one this kind brings; its default when not given")]
    public string? Kubernetes { get; init; }

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

sealed class UpgradeSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    public string Name { get; init; } = "";

    [CommandOption("--kubernetes <version>")]
    [Description("The version to go to; the newest this kind brings when not given")]
    public string? Kubernetes { get; init; }
}

sealed class ClusterUpgradeCommand : AsyncCommand<UpgradeSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpgradeSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Upgrade(Clusters.ValidName(s.Name), s.Kubernetes);
        return 0;
    }
}

static class Files
{
    public static string? Read(string? file) => file is null ? null : file == "-" ? Console.In.ReadToEnd()
        : System.IO.File.Exists(file) ? System.IO.File.ReadAllText(file) : throw new UsageException($"{file} does not exist");
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
        await new Clusters(Console.WriteLine).Create(s.Name, s.Workers, Clusters.Addons(s.Addons), Clusters.Storage(s.Storage), !s.NoLoadBalancer, Clusters.Ports(s.Ports), Files.Read(s.File), s.Kubernetes);
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

sealed class EditSettings : CommandSettings
{
    [CommandArgument(0, "<name>")]
    [Description("The cluster: lower case letters, digits, . and -")]
    public string Name { get; init; } = "";

    [CommandOption("--addons <names>")]
    [Description("All the add-ons it should have, comma separated: metrics-server, traefik, cert-manager, victoria-metrics, headlamp; none for none")]
    public string? Addons { get; init; }

    [CommandOption("--storage <kind>")]
    [Description("none or nfs; the host folder needs recreate")]
    public string? Storage { get; init; }
}

sealed class ClusterEditCommand : AsyncCommand<EditSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EditSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).Edit(s.Name, s.Addons == "none" ? "" : s.Addons, s.Storage);
        return 0;
    }
}

sealed class ClusterDashboardTokenCommand : AsyncCommand<NameSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, NameSettings s, CancellationToken cancellation)
    {
        await new Clusters(Console.WriteLine).DashboardToken(s.Name);
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

sealed class VersionCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellation)
    {
        Console.WriteLine(Commands.Version);
        return 0;
    }
}
