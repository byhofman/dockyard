using Dockyard.Dashboard.Features.Setup;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Tests;

// The facade as the screens see it, over the prototype's Mac rows: it writes down every call, reports the given lines,
// and holds each run open until Release.
sealed class RecordingSetup(bool installed = true) : ISetupSource
{
    TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Dictionary<string, string> values = [];

    public List<string> Calls { get; } = [];
    public IReadOnlyList<SetupProgress> Lines { get; set; } = [];
    public bool Installed { get; set; } = installed;
    public string CaPem { get; set; } = "PEM";

    public bool IsInstalled => Installed;
    public string SelfId => "DockyardCli";
    public IReadOnlyList<SettingDefinition> SettingDefinitions => global::Dockyard.Setup.SettingDefinitions.All;
    public IReadOnlyList<ComponentRow>? Rows { get; set; }
    public IReadOnlyList<ComponentRow> Components() => Rows ?? PrototypeScreens.MacRows(Installed);
    public Task<IReadOnlyList<ComponentRow>> ComponentsAsync(CancellationToken ct) => Task.FromResult(Components());

    public string GetSetting(string key) => values.TryGetValue(key, out var v) ? v
        : SettingDefinitions.First(d => d.Key == key) switch
        {
            { Kind: SettingKind.Choice, Choices: [var first, ..] } => first,
            { Kind: SettingKind.EngineFile } => "{}",
            _ => global::Dockyard.Setup.SettingDefinitions.Off,
        };

    public void Set(string key, string value) => values[key] = value;

    public void Release()
    {
        var done = hold;
        hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        done.TrySetResult();
    }

    async Task Run(string call, IProgress<SetupProgress> progress)
    {
        Calls.Add(call);
        foreach (var line in Lines) progress.Report(line);
        await hold.Task;
    }

    public Task SetSetting(string key, string value, IProgress<SetupProgress> progress) { values[key] = value; return Run($"set {key} {value}", progress); }
    public async Task<int> Setup(SetupRequest request, IProgress<SetupProgress> progress, CancellationToken ct)
    {
        await Run("setup " + string.Join(",", request.With), progress);
        Installed = true;
        return DockyardSetup.Ok;
    }
    public Task Add(string id, IProgress<SetupProgress> progress) => Run("add " + id, progress);
    public Task Remove(string id, bool removeData, IProgress<SetupProgress> progress) => Run("remove " + id, progress);
    public Task Update(IReadOnlyList<string>? ids, IProgress<SetupProgress> progress) => Run("update " + string.Join(",", ids ?? []), progress);
    public Task Uninstall(bool removeData, IProgress<SetupProgress> progress) => Run("uninstall", progress);
    public Task Import(IProgress<SetupProgress> progress) => Run("import", progress);

    public Task StartEngine(string id) { Calls.Add("start " + id); return Task.CompletedTask; }
    public Task UseEngine(string id) { Calls.Add("use " + id); return Task.CompletedTask; }
    public Task<string> ClusterCa(string cluster) { Calls.Add("ca " + cluster); return Task.FromResult(CaPem); }
    public Task PushClusterWants() { Calls.Add("wants"); return Task.CompletedTask; }
}
