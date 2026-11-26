using System.ComponentModel;
using Dockyard.Core;
using Dockyard.Setup;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

static class Names
{
    public static IReadOnlyList<string> Split(IEnumerable<string> values) =>
        values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
}

sealed class SetupSettings : CommandSettings
{
    [CommandOption("--silent")]
    [Description("Do not ask: install, or bring what is installed up to date, and report on the console")]
    public bool Silent { get; init; }

    [CommandOption("--with <name>")]
    [Description("Add this component, also on an existing install; repeat it or separate names with commas")]
    public string[] With { get; init; } = [];

    [CommandOption("--without <name>")]
    [Description("Leave this component out, or remove it from an existing install")]
    public string[] Without { get; init; } = [];

    [CommandOption("--source <source>")]
    [Description("Where docker, kind, kubectl and helm come from: dockyard or package-manager; remembered")]
    public string? Source { get; init; }

    [CommandOption("--trust-https")]
    [Description("Add Dockyard's own certificate to this computer's trust store, for HTTPS on container domains")]
    public bool TrustHttps { get; init; }

    [CommandOption("--kubectl-shell")]
    [Description("Set up kubectl in your shell: k, completion, $do and $now")]
    public bool KubectlShell { get; init; }

    [CommandOption("--no-start")]
    [Description("Do not start Dockyard afterwards")]
    public bool NoStart { get; init; }

    [CommandOption("--image <file>")]
    [Description("Windows: the Linux engine image to import instead of downloading it")]
    public string? Image { get; init; }

    [CommandOption("--remove-data")]
    [Description("With --without: remove the images and containers of what is removed too")]
    public bool RemoveData { get; init; }

    [CommandOption("--assets <folder>")]
    [Description("Take Dockyard's own release files from this folder instead of downloading them, unchecked")]
    public string? Assets { get; init; }

    [CommandOption("--resume")]
    [Description("Finish a setup that waited for a reboot; the installer schedules this itself")]
    public bool Resume { get; init; }

    [CommandOption("--open-dashboard")]
    [Description("Open the dashboard's setup screen when done")]
    public bool OpenDashboard { get; init; }

    public override ValidationResult Validate()
    {
        if (Source is not null && ToolSourceArgument.Parse(Source) is null) return ValidationResult.Error("--source is dockyard or package-manager");
        if (Assets is not null && !Directory.Exists(Assets)) return ValidationResult.Error("--assets needs a folder that exists");
        return ValidationResult.Success();
    }
}

sealed class SetupCommand(CliEnvironment env) : AsyncCommand<SetupSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SetupSettings s, CancellationToken cancellation)
    {
        var asks = !s.Silent && !s.Resume;
        if (asks && !env.HasTerminal)
        {
            env.Error.WriteLine("Setup asks which components you want, and there is no terminal to ask in: run dockyard setup in a terminal, or add --silent to install without asking.");
            return DockyardSetup.WrongArguments;
        }
        if (s.Assets is not null) DockyardSetup.LocalAssets = Path.GetFullPath(s.Assets);

        using var setup = env.Setup();
        IReadOnlyList<string> with = Names.Split(s.With);
        IReadOnlyList<string> without = Names.Split(s.Without);
        if (asks && env.HasTerminal && with.Count == 0 && without.Count == 0)
            (with, without) = Ask(setup);

        var request = new SetupRequest(with, without, ToolSourceArgument.Parse(s.Source), s.TrustHttps, s.KubectlShell, s.Silent || s.Resume, s.Resume)
        {
            Start = !s.NoStart,
            Image = s.Image,
            RemoveData = s.RemoveData,
            Headless = !env.HasDesktop,
        };
        var code = await setup.Setup(request, new ProgressRenderer(env.Console, env.Error), cancellation);
        if (code == DockyardSetup.Ok && (s.OpenDashboard || asks && env.HasDesktop && setup.HasDashboard))
            env.Dashboard.Open(setup.DashboardExe, "setup");
        return code;
    }

    // A new install on a computer without a desktop is not offered the tray and the dashboard; the library leaves them out.
    (IReadOnlyList<string> With, IReadOnlyList<string> Without) Ask(DockyardSetup setup)
    {
        var offered = setup.Components().Where(c => c.Flag is not null).ToList();
        if (!setup.IsInstalled && !env.HasDesktop)
            offered = offered.Where(c => c.Flag is not (DockyardSetup.TrayFlag or DockyardSetup.DashboardFlag)).ToList();
        if (offered.Count == 0) return ([], []);

        var prompt = new MultiSelectionPrompt<ComponentRow>()
            .Title("Which optional components should Dockyard have?")
            .NotRequired()
            .InstructionsText("[grey](space toggles, enter installs)[/]")
            .UseConverter(row => Markup.Escape($"{row.Title} ({row.Flag})"));
        foreach (var row in offered)
        {
            var item = prompt.AddChoice(row);
            if (setup.IsInstalled ? !row.CanAdd : true) item.Select();
        }
        var chosen = env.Console.Prompt(prompt).Select(c => c.Flag!).ToList();
        return (chosen, offered.Select(c => c.Flag!).Where(f => !chosen.Contains(f)).ToList());
    }
}

class ComponentNameSettings : CommandSettings
{
    [CommandArgument(0, "<flag>")]
    [Description("The component, by the name component ls shows")]
    public string Flag { get; init; } = "";
}

sealed class ComponentRemoveSettings : ComponentNameSettings
{
    [CommandOption("--remove-data")]
    [Description("Take the images and containers of an engine along")]
    public bool RemoveData { get; init; }
}

sealed class ComponentListCommand(CliEnvironment env) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        var table = new Table().Border(TableBorder.None).AddColumns("FLAG", "COMPONENT", "INSTALLED", "NEWEST", "SOURCE", "NOTE");
        foreach (var row in await setup.ComponentsAsync(cancellation))
            table.AddRow(
                Markup.Escape(row.Flag ?? "-"),
                Markup.Escape(row.Title),
                Markup.Escape(row.Installed ?? "-"),
                Markup.Escape(row.Newest ?? "-"),
                Markup.Escape(row.Source),
                Markup.Escape(row.RequiredBecause ?? row.Needs ?? ""));
        env.Console.Write(table);
        return 0;
    }
}

sealed class ComponentAddCommand(CliEnvironment env) : AsyncCommand<ComponentNameSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ComponentNameSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.Add(s.Flag, new ProgressRenderer(env.Console, env.Error));
        return 0;
    }
}

sealed class ComponentRemoveCommand(CliEnvironment env) : AsyncCommand<ComponentRemoveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ComponentRemoveSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.Remove(s.Flag, s.RemoveData, new ProgressRenderer(env.Console, env.Error));
        return 0;
    }
}

sealed class UpdateSettings : CommandSettings
{
    [CommandOption("--check")]
    [Description("Only list what has an update")]
    public bool Check { get; init; }
}

sealed class UpdateCommand(CliEnvironment env) : AsyncCommand<UpdateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        var checks = await setup.CheckUpdates(cancellation);
        foreach (var check in checks)
        {
            if (check.Error is { } problem) { env.Error.WriteLine($"{check.Row.Title}: {problem}"); continue; }
            if (check.Row.Installed is not { } installed) continue;
            env.Console.WriteLine($"{check.Row.Title}\t{installed}\t{(check.Row.CanUpdate ? $"{check.Row.Newest} available" : "up to date")}");
        }
        if (!s.Check) await setup.UpdateAvailable(checks, new ProgressRenderer(env.Console, env.Error));
        return 0;
    }
}

sealed class UninstallSettings : CommandSettings
{
    [CommandOption("--remove-data")]
    [Description("Remove the images and containers too; they stay otherwise")]
    public bool RemoveData { get; init; }
}

sealed class UninstallCommand(CliEnvironment env) : AsyncCommand<UninstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UninstallSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.Uninstall(s.RemoveData, new ProgressRenderer(env.Console, env.Error), silent: !env.HasTerminal);
        return 0;
    }
}

sealed class ImportSettings : CommandSettings
{
    [CommandOption("--from <endpoint>")]
    [Description("The engine to copy from instead of Docker Desktop: unix://, npipe:// or tcp://")]
    public string? From { get; init; }
}

sealed class ImportCommand(CliEnvironment env) : AsyncCommand<ImportSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ImportSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.Import(new ProgressRenderer(env.Console, env.Error), s.From);
        return 0;
    }
}
