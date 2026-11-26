using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// Stands in for DockyardSetup until the dashboard is wired to it: installed, nothing listed, and every action refused.
sealed class StubSetupSource : ISetupSource
{
    const string NotYet = "The dashboard does not manage Dockyard yet. Use the Toolbox from the tray for now.";

    public bool IsInstalled => true;
    public string SelfId => "DockyardCli";
    public IReadOnlyList<SettingDefinition> SettingDefinitions => global::Dockyard.Setup.SettingDefinitions.All;

    public IReadOnlyList<ComponentRow> Components() => [];
    public Task<IReadOnlyList<ComponentRow>> ComponentsAsync(CancellationToken ct) => Task.FromResult(Components());
    public string GetSetting(string key) => SettingDefinitions.FirstOrDefault(d => d.Key == key) switch
    {
        { Kind: SettingKind.Choice, Choices: [var first, ..] } => first,
        { Kind: SettingKind.EngineFile } => "{}",
        _ => global::Dockyard.Setup.SettingDefinitions.Off,
    };
    public Task SetSetting(string key, string value, IProgress<SetupProgress> progress) => throw new InvalidOperationException(NotYet);
    public Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct) => throw new InvalidOperationException(NotYet);
    public Task Add(string id, IProgress<SetupProgress> progress) => throw new InvalidOperationException(NotYet);
    public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress) => throw new InvalidOperationException(NotYet);
    public Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress) => throw new InvalidOperationException(NotYet);
    public Task Uninstall(bool removeData, IProgress<SetupProgress> progress) => throw new InvalidOperationException(NotYet);
    public Task Import(IProgress<SetupProgress> progress) => throw new InvalidOperationException(NotYet);
}
