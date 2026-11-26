using Dockyard.Core;
using Dockyard.Hosts;
using static Dockyard.Setup.SettingDefinitions;

namespace Dockyard.Setup;

public sealed partial class DockyardSetup
{
    public IReadOnlyList<SettingDefinition> SettingDefinitions => global::Dockyard.Setup.SettingDefinitions.For(host.Engines);

    SettingDefinition Definition(string key) => SettingDefinitions.FirstOrDefault(d => d.Key == key)
        ?? throw new ArgumentException($"There is no setting {key}. Settings: {string.Join(", ", SettingDefinitions.Select(d => d.Key))}");

    // As SetSetting takes it: on or off, one of the choices, or an engine's daemon.json.
    public string GetSetting(string key)
    {
        var definition = Definition(key);
        settings.Reload();
        return key switch
        {
            ContainerDomains => Toggle(settings.ContainerDomains),
            TrustedHttps => Toggle(settings.HttpsEnabled),
            StartEngineAtLogon => Toggle(settings.StartActiveAtLogon),
            StopOtherEngineOnSwitch => Toggle(settings.StopOtherOnSwitch),
            KubectlShell => Toggle(settings.KubectlShell),
            Source => definition.Choices![settings.Source == ToolSource.PackageManager ? 0 : 1],
            _ => Task.Run(() => host.ReadEngineSettings(EngineOf(key))).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException($"The {EngineOf(key)} engine's settings are not Dockyard's to show"),
        };
    }

    // Why SetSetting would refuse this value, or null when it would take it.
    public async Task<string?> CheckSetting(string key, string value)
    {
        var definition = Definition(key);
        if ((ValueProblem(definition, value) ?? StateProblem(definition, value)) is { } problem) return problem;
        return definition.Kind == SettingKind.EngineFile ? await host.ValidateEngineSettings(EngineOf(key), value) : null;
    }

    // The keys of an engine's settings that Dockyard sets itself, so that these replace the ones given.
    public IReadOnlyList<string> Overridden(string key, string json) => EngineSettings.Overridden(EngineOf(Definition(key).Key), json);

    // Validates, saves and applies. A setting that needs an administrator asks for one where the system does.
    public async Task SetSetting(string key, string value, IProgress<SetupProgress> progress)
    {
        var definition = Definition(key);
        // Another front end may have changed them since this one started.
        settings.Reload();
        if (ValueProblem(definition, value) is { } wrong) throw new ArgumentException(wrong);
        if (StateProblem(definition, value) is { } refused) throw new InvalidOperationException(refused);
        var log = Log(progress);
        var on = value == On;
        switch (key)
        {
            case ContainerDomains:
                if (settings.ContainerDomains == on) return;
                settings.ContainerDomains = on;
                settings.Save();
                if (host.IsInstalled) await Https.SetNames(host, on, log);
                break;
            case TrustedHttps:
                if (settings.HttpsEnabled == on) return;
                if (on) await Https.Enable(host, settings, log, silent: false);
                else await Https.Disable(host, settings, log, silent: false);
                break;
            case StartEngineAtLogon:
                settings.StartActiveAtLogon = on;
                settings.Save();
                break;
            case StopOtherEngineOnSwitch:
                settings.StopOtherOnSwitch = on;
                settings.Save();
                break;
            case KubectlShell:
                settings.KubectlShell = on;
                settings.Save();
                if (!host.IsInstalled) return;
                ApplyShell(log);
                if (settings.KubectlShell) log($"kubectl is set up in your shell: open a new terminal for k, completion, {ShellSetup.DoAndNowHere}");
                break;
            case Source:
                var source = string.Equals(value, definition.Choices![0], StringComparison.OrdinalIgnoreCase) ? ToolSource.PackageManager : ToolSource.Dockyard;
                if (settings.Source == source) return;
                settings.Source = source;
                settings.Save();
                if (host.IsInstalled) await MoveTools(log);
                break;
            default:
                var engine = EngineOf(key);
                if (await host.ValidateEngineSettings(engine, value) is { } problem) throw new InvalidOperationException(problem);
                foreach (var overridden in EngineSettings.Overridden(engine, value))
                    progress.Report(new SetupProgress($"{overridden}: Dockyard sets this one itself, so yours is replaced", SetupProgressKind.Warning));
                await host.ApplyEngineSettings(log, engine, value);
                break;
        }
    }

    static string Toggle(bool on) => on ? On : Off;

    static string EngineOf(string key) => key[EngineFilePrefix.Length..];

    static string? ValueProblem(SettingDefinition definition, string value) => definition.Kind switch
    {
        SettingKind.Toggle when value is not (On or Off) => $"{definition.Key} is {On} or {Off}",
        SettingKind.Choice when !definition.Choices!.Contains(value, StringComparer.OrdinalIgnoreCase) => $"{definition.Key} is one of: {string.Join(", ", definition.Choices!)}",
        _ => null,
    };

    string? StateProblem(SettingDefinition definition, string value)
    {
        if (value != On) return null;
        if (definition.LinuxEngineOnly && !HasLinuxEngine)
            return $"{definition.Title}: for the Linux engine only, and there is no Linux engine here. Add the Linux engine first.";
        if (definition.Key == TrustedHttps && !settings.ContainerDomains)
            return "Trusted HTTPS is for container domains, which are off: turn them on first.";
        return null;
    }

    bool HasLinuxEngine => host.Engines.Any(e => e.Id == EngineSettings.Linux && (e.ComponentId is not { } id || !settings.RemovedComponents.Contains(id)));

    // Every tool that is there comes from the new source at once, so there is never one of each on the PATH.
    async Task MoveTools(Action<string> log)
    {
        log(settings.Source == ToolSource.PackageManager ? "Moving the tools to the package manager" : "Moving the tools to Dockyard");
        List<(string Component, Release? Release)> present = [];
        // On Linux docker, Compose and Buildx are the distribution's packages whatever this says.
        foreach (var tool in OperatingSystem.IsLinux() ? Tools.Standalone : Tools.All)
            if (await host.Installed(tool.Id) is not null) present.Add((tool.Id, null));
        if (present.Count > 0 && await host.Update(log, present)) ExitRequested?.Invoke();
    }
}
