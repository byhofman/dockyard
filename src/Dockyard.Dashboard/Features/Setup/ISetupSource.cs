using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// The part of DockyardSetup the Setup, Tools and Settings screens use, with its own names and types, so the screens can
// be drawn over fictive rows and settings.
interface ISetupSource
{
    bool IsInstalled { get; }
    string SelfId { get; }
    IReadOnlyList<SettingDefinition> SettingDefinitions { get; }

    IReadOnlyList<ComponentRow> Components();
    Task<IReadOnlyList<ComponentRow>> ComponentsAsync(CancellationToken ct);
    string GetSetting(string key);
    Task SetSetting(string key, string value, IProgress<SetupProgress> progress);
    Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct);
    Task Add(string id, IProgress<SetupProgress> progress);
    Task Remove(string id, bool removeData, IProgress<SetupProgress> progress);
    Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress);
    Task Uninstall(bool removeData, IProgress<SetupProgress> progress);
    Task Import(IProgress<SetupProgress> progress);
}
