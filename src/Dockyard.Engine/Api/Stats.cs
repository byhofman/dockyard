using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public async IAsyncEnumerable<ContainerUsage> StatsAsync(string id, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var res = await Send(HttpMethod.Get, $"containers/{Esc(id)}/stats?stream=1", null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var sample in JsonLines.ReadAsync(stream, StatsJson.Default.StatsSample, ct))
            yield return StatsCalculator.Calculate(sample, IsWindows);
    }
}

[JsonSerializable(typeof(StatsSample))]
partial class StatsJson : JsonSerializerContext;
