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
    [Description("Open the dashboard at Tools when done")]
    public bool OpenDashboard { get; init; }

    public override ValidationResult Validate()
    {
        SetupArguments.Parse(Arguments(), out var problems);
        return problems.Count == 0 ? ValidationResult.Success() : ValidationResult.Error(problems[0]);
    }

    public IReadOnlyList<string> Arguments()
    {
        var args = new List<string>();
        void Switch(bool on, string name) { if (on) args.Add(name); }
        void Value(string name, string? value) { if (value is not null) args.AddRange([name, value]); }

        Switch(Silent, "--silent");
        foreach (var name in With) Value("--with", name);
        foreach (var name in Without) Value("--without", name);
        Value("--source", Source);
        Switch(TrustHttps, "--trust-https");
        Switch(KubectlShell, "--kubectl-shell");
        Switch(NoStart, "--no-start");
        Value("--image", Image);
        Switch(RemoveData, "--remove-data");
        Value("--assets", Assets);
        Switch(Resume, "--resume");
        Switch(OpenDashboard, "--open-dashboard");
        return args;
    }
}

sealed class SetupCommand(CliEnvironment env) : AsyncCommand<SetupSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SetupSettings s, CancellationToken cancellation)
    {
        var parsed = SetupArguments.Parse(s.Arguments(), out _);
        var asks = !parsed.Silent;
        if (asks && !env.HasTerminal)
        {
            env.Error.WriteLine("Setup asks which components you want, and there is no terminal to ask in: run dockyard setup in a terminal, or add --silent to install without asking.");
            return DockyardSetup.WrongArguments;
        }
        if (parsed.Assets is not null) DockyardSetup.LocalAssets = parsed.Assets;

        using var setup = env.Setup();
        var request = parsed with { Headless = !env.HasDesktop };
        if (asks && env.HasTerminal && request.With.Count == 0 && request.Without.Count == 0)
        {
            var (with, without) = Ask(setup);
            request = request with { With = with, Without = without };
        }
        var code = await setup.Setup(request, new ProgressRenderer(env.Console, env.Error), cancellation);
        if (code == DockyardSetup.Ok && (parsed.OpenDashboard || asks && env.HasDesktop && setup.HasDashboard))
            env.Dashboard.Open(setup.DashboardExe, "tools");
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
            if (row.Default) item.Select();
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
        var table = new CliTable("FLAG", "COMPONENT", "INSTALLED", "NEWEST", "SOURCE", "NOTE");
        foreach (var row in await setup.ComponentsAsync(cancellation))
            table.Add(row.Flag, row.Title, row.Installed ?? "not installed", row.Newest, row.Source, Note(row));
        table.Write(env.Console);
        return 0;
    }

    // As the dashboard's Tools says it: why a component has to stay, or what it needs.
    internal static string? Note(ComponentRow row) =>
        row.RequiredBecause is { Length: > 0 } why ? $"Required: {why}" : row.Needs;
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
    [CommandArgument(0, "[component]")]
    [Description("Only these, by the name component ls shows; without one, everything with an update")]
    public string[] Components { get; init; } = [];

    [CommandOption("--check")]
    [Description("Only list each component's version and newest version; update nothing")]
    public bool Check { get; init; }
}

sealed class UpdateCommand(CliEnvironment env) : AsyncCommand<UpdateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        if (s.Components.Length > 0 && !s.Check)
        {
            await setup.Update(Names.Split(s.Components), new ProgressRenderer(env.Console, env.Error));
            return 0;
        }
        var checks = await setup.CheckUpdates(cancellation);
        var table = new CliTable("COMPONENT", "INSTALLED", "NEWEST", "SOURCE", "STATUS");
        foreach (var check in checks)
        {
            if (check.Error is { } problem) { env.Error.WriteLine($"{check.Row.Title}: {problem}"); continue; }
            if (check.Row.Installed is not { } installed) continue;
            table.Add(check.Row.Title, installed, check.Row.Newest, check.Row.Source, check.Row.CanUpdate ? "update available" : "up to date");
        }
        table.Write(env.Console);
        if (!s.Check) await setup.UpdateAvailable(checks, new ProgressRenderer(env.Console, env.Error));
        return checks.Any(c => c.Error is not null) ? DockyardSetup.Failed : DockyardSetup.Ok;
    }
}

sealed class UninstallSettings : CommandSettings
{
    [CommandOption("--silent")]
    [Description("Do not ask anything, also in a terminal")]
    public bool Silent { get; init; }

    [CommandOption("--remove-data")]
    [Description("Remove the images and containers too; they stay otherwise")]
    public bool RemoveData { get; init; }
}

sealed class UninstallCommand(CliEnvironment env) : AsyncCommand<UninstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UninstallSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.Uninstall(s.RemoveData, new ProgressRenderer(env.Console, env.Error), silent: SetupArguments.Silent(s.Silent, env.HasTerminal));
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
