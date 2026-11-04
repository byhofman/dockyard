using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<SystemDf> DiskUsageAsync(CancellationToken ct = default) =>
        GetJson("system/df", SystemJson.Default.SystemDf, ct);

    public Task<PruneResult> PruneContainersAsync(CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "containers/prune", null, CoreJson.Default.PruneResult, ct);

    public Task<PruneResult> PruneBuildCacheAsync(CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "build/prune", null, CoreJson.Default.PruneResult, ct);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SystemDf))]
partial class SystemJson : JsonSerializerContext;
