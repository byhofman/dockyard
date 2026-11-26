using System.ComponentModel;
using Dockyard.Setup;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Dockyard.Cli;

class SettingKeySettings : CommandSettings
{
    [CommandArgument(0, "<key>")]
    [Description("A setting, by the key settings ls shows")]
    public string Key { get; init; } = "";
}

sealed class SettingSetSettings : SettingKeySettings
{
    [CommandArgument(1, "[value]")]
    [Description("on or off, or one of the choices; an engine's settings open in your editor without one")]
    public string? Value { get; init; }
}

sealed class SettingsListCommand(CliEnvironment env) : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        var table = new Table().Border(TableBorder.None).AddColumns("KEY", "VALUE", "SETTING");
        foreach (var definition in setup.SettingDefinitions)
            table.AddRow(
                Markup.Escape(definition.Key),
                Markup.Escape(definition.Kind == SettingKind.EngineFile ? "daemon.json" : setup.GetSetting(definition.Key)),
                Markup.Escape(definition.LinuxEngineOnly ? $"{definition.Title} (Linux engine only)" : definition.Title));
        env.Console.Write(table);
        return 0;
    }
}

sealed class SettingsGetCommand(CliEnvironment env) : Command<SettingKeySettings>
{
    public override int Execute(CommandContext context, SettingKeySettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        env.Console.WriteLine(setup.GetSetting(s.Key));
        return 0;
    }
}

sealed class SettingsSetCommand(CliEnvironment env) : AsyncCommand<SettingSetSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SettingSetSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        if (s.Key.StartsWith(SettingDefinitions.EngineFilePrefix, StringComparison.Ordinal))
        {
            if (s.Value is not null) throw new ArgumentException("An engine's settings are a file: dockyard config apply --file <file>, or leave the value out to edit them");
            return await ConfigEdit.Run(setup, s.Key[SettingDefinitions.EngineFilePrefix.Length..], env);
        }
        await setup.SetSetting(s.Key, s.Value ?? throw new UsageException($"{s.Key} needs a value"), new ProgressRenderer(env.Console, env.Error));
        return 0;
    }
}

sealed class EngineSettings : CommandSettings
{
    [CommandArgument(0, "<engine>")]
    [Description("linux, or win on Windows")]
    public string Engine { get; init; } = "";
}

sealed class EngineListCommand(CliEnvironment env) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        var active = setup.ActiveEngine();
        foreach (var engine in setup.Engines)
            env.Console.WriteLine($"{(engine.Id == active ? "*" : " ")} {engine.Id}\t{engine.Title}\t{(await setup.IsAnswering(engine.Id) ? "running" : "stopped")}");
        return 0;
    }
}

sealed class EngineStartCommand(CliEnvironment env) : AsyncCommand<EngineSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EngineSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.StartEngine(s.Engine);
        env.Console.WriteLine($"{s.Engine} runs");
        return 0;
    }
}

sealed class EngineStopCommand(CliEnvironment env) : AsyncCommand<EngineSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EngineSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.StopEngine(s.Engine);
        env.Console.WriteLine($"{s.Engine} is stopped");
        return 0;
    }
}

sealed class EngineUseCommand(CliEnvironment env) : AsyncCommand<EngineSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, EngineSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.UseEngine(s.Engine);
        env.Console.WriteLine($"docker uses {s.Engine}");
        return 0;
    }
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

static class EngineFile
{
    public static string Key(DockyardSetup setup, string? engine) => SettingDefinitions.EngineFilePrefix + (engine ?? setup.ActiveEngine());
}

sealed class ConfigShowCommand(CliEnvironment env) : Command<ConfigSettings>
{
    public override int Execute(CommandContext context, ConfigSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        env.Console.WriteLine(setup.GetSetting(EngineFile.Key(setup, s.Engine)));
        return 0;
    }
}

sealed class ConfigValidateCommand(CliEnvironment env) : AsyncCommand<ConfigFileSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigFileSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        var key = EngineFile.Key(setup, s.Engine);
        var json = Files.Read(s.File)!;
        if (await setup.CheckSetting(key, json) is { } problem)
        {
            env.Error.WriteLine(problem);
            return 1;
        }
        foreach (var overridden in setup.Overridden(key, json)) env.Console.WriteLine($"{overridden}: Dockyard sets this one itself, so yours is replaced");
        env.Console.WriteLine("The engine accepts these settings");
        return 0;
    }
}

sealed class ConfigApplyCommand(CliEnvironment env) : AsyncCommand<ConfigFileSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigFileSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.SetSetting(EngineFile.Key(setup, s.Engine), Files.Read(s.File)!, new ProgressRenderer(env.Console, env.Error));
        return 0;
    }
}

sealed class ConfigEditCommand(CliEnvironment env) : AsyncCommand<ConfigSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ConfigSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        return await ConfigEdit.Run(setup, s.Engine ?? setup.ActiveEngine(), env);
    }
}

sealed class HttpsSettings : CommandSettings
{
    [CommandArgument(0, "<on|off>")]
    public string State { get; init; } = "";

    public override ValidationResult Validate() => State is "on" or "off" ? ValidationResult.Success() : ValidationResult.Error("https takes on or off");
}

sealed class HttpsCommand(CliEnvironment env) : AsyncCommand<HttpsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, HttpsSettings s, CancellationToken cancellation)
    {
        using var setup = env.Setup();
        await setup.SetSetting(SettingDefinitions.TrustedHttps, s.State, new ProgressRenderer(env.Console, env.Error));
        return 0;
    }
}
