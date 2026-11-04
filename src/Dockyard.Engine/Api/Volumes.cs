using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public async Task<List<Volume>> ListVolumesAsync(CancellationToken ct = default) =>
        (await GetJson("volumes", VolumeJson.Default.VolumeList, ct)).Volumes ?? [];

    public Task RemoveVolumeAsync(string name, bool force = false, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Delete, $"volumes/{Esc(name)}?force={Bool(force)}", null, ct);

    public Task<PruneResult> PruneVolumesAsync(CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "volumes/prune?filters=" + Filters(("all", "true")), null, CoreJson.Default.PruneResult, ct);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VolumeList))]
partial class VolumeJson : JsonSerializerContext;
