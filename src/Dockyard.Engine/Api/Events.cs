using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public async IAsyncEnumerable<DockerEvent> EventsAsync(
        IReadOnlyCollection<string>? types = null, DateTimeOffset? since = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var query = new List<string>();
        if (since is { } s) query.Add($"since={s.ToUnixTimeSeconds()}");
        if (types is { Count: > 0 }) query.Add("filters=" + Filters(types.Select(t => ("type", t)).ToArray()));
        var path = "events" + (query.Count > 0 ? "?" + string.Join('&', query) : "");

        using var res = await Send(HttpMethod.Get, path, null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var e in JsonLines.ReadAsync(stream, EventJson.Default.DockerEvent, ct)) yield return e;
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DockerEvent))]
partial class EventJson : JsonSerializerContext;
