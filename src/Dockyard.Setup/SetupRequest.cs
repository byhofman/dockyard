using Dockyard.Core;

namespace Dockyard.Setup;

// With and Without name components by flag or id. Neither: a new install gets everything, an existing one keeps what it has.
// Source null keeps what an earlier install chose, and KubectlShell false leaves the shell setting as it is. Silent: nothing
// may pop up.
public sealed record SetupRequest(IReadOnlyList<string> With, IReadOnlyList<string> Without, ToolSource? Source, bool TrustHttps, bool KubectlShell, bool Silent, bool Resume)
{
    // Start Dockyard afterwards.
    public bool Start { get; init; } = true;
    // Windows: the Linux engine image to import instead of downloading it.
    public string? Image { get; init; }
    // What Without removes from an existing install takes its images and containers along.
    public bool RemoveData { get; init; }
    // No desktop session here: a new install leaves the tray and the dashboard out unless With names them.
    public bool Headless { get; init; }

    // What a front end's setup screen asks for, from its components and the values of its setting rows, keyed as
    // SettingDefinitions names them. A source it does not show keeps what an earlier install chose.
    public static SetupRequest FromSettings(IReadOnlyList<string> with, IReadOnlyList<string> without, IReadOnlyDictionary<string, string> values) => new(
        with,
        without,
        values.GetValueOrDefault(SettingDefinitions.Source) switch
        {
            null => null,
            SettingDefinitions.DockyardSource => ToolSource.Dockyard,
            _ => ToolSource.PackageManager,
        },
        TrustHttps: values.GetValueOrDefault(SettingDefinitions.TrustedHttps) == SettingDefinitions.On,
        KubectlShell: values.GetValueOrDefault(SettingDefinitions.KubectlShell) == SettingDefinitions.On,
        Silent: false,
        Resume: false);
}
