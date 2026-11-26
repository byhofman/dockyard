using Dockyard.Setup;

namespace Dockyard.Dashboard.Features.Setup;

// The real facade, one for the whole dashboard.
sealed class DockyardSetupSource(DockyardSetup setup) : ISetupSource
{
    public bool IsInstalled => setup.IsInstalled;
    public string SelfId => setup.SelfId;
    public IReadOnlyList<SettingDefinition> SettingDefinitions => setup.SettingDefinitions;

    public IReadOnlyList<ComponentRow> Components() => setup.Components();
    public Task<IReadOnlyList<ComponentRow>> ComponentsAsync(CancellationToken ct) => setup.ComponentsAsync(ct);
    public string GetSetting(string key) => setup.GetSetting(key);
    public Task SetSetting(string key, string value, IProgress<SetupProgress> progress) => setup.SetSetting(key, value, progress);
    public Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct) => setup.Setup(request, progress, ct);
    public Task Add(string id, IProgress<SetupProgress> progress) => setup.Add(id, progress);
    public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress) => setup.Remove(id, removeData, progress);
    public Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress) => setup.Update(ids, progress);
    public Task Uninstall(bool removeData, IProgress<SetupProgress> progress) => setup.Uninstall(removeData, progress);
    public Task Import(IProgress<SetupProgress> progress) => setup.Import(progress);
    public Task StartEngine(string id) => setup.StartEngine(id);
    public Task UseEngine(string id) => setup.UseEngine(id);
    public Task<string> ClusterCa(string cluster) => setup.ClusterCa(cluster);
    public Task PushClusterWants() => setup.PushClusterWants();
}
