using System.Net;
using System.Text.Json.Serialization;
using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<List<ImageSummary>> ListImagesAsync(CancellationToken ct = default) =>
        GetJson("images/json", ImageJson.Default.ListImageSummary, ct);

    public async Task RemoveImageAsync(string image, bool force = false, CancellationToken ct = default)
    {
        using var _ = await Send(HttpMethod.Delete, $"images/{Esc(image)}?force={Bool(force)}", null, stream: false, ct);
    }

    public Task<PruneResult> PruneImagesAsync(bool danglingOnly = true, CancellationToken ct = default) =>
        SendJson(HttpMethod.Post, "images/prune?filters=" + Filters(("dangling", danglingOnly ? "true" : "false")), null,
            CoreJson.Default.PruneResult, ct);

    public async Task PullImageAsync(string reference, IProgress<PullProgress>? progress = null, CancellationToken ct = default)
    {
        var (image, tag) = SplitReference(reference);
        var path = $"images/create?fromImage={Esc(image)}" + (tag.Length > 0 ? $"&tag={Esc(tag)}" : "");
        using var res = await Send(HttpMethod.Post, path, null, stream: true, ct);
        await using var stream = await res.Content.ReadAsStreamAsync(ct);

        var layers = new Dictionary<string, (long Current, long Total)>();
        await foreach (var m in JsonLines.ReadAsync(stream, ImageJson.Default.PullMessage, ct))
        {
            if (m.Error is { } error) throw new DockerException(HttpStatusCode.InternalServerError, error);

            if (m.Id is { } id && m.ProgressDetail is { Total: > 0 } detail)
                layers[id] = (detail.Current, detail.Total);
            else if (m.Id is { } done && m.Status is "Pull complete" or "Already exists" && layers.TryGetValue(done, out var layer))
                layers[done] = (layer.Total, layer.Total);

            progress?.Report(new PullProgress(m.Status ?? "", layers.Values.Sum(l => l.Current), layers.Values.Sum(l => l.Total)));
        }
    }

    internal static (string Image, string Tag) SplitReference(string reference)
    {
        if (reference.Contains('@')) return (reference, "");
        var slash = reference.LastIndexOf('/');
        var colon = reference.LastIndexOf(':');
        return colon > slash ? (reference[..colon], reference[(colon + 1)..]) : (reference, "latest");
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ImageSummary>))]
[JsonSerializable(typeof(PullMessage))]
partial class ImageJson : JsonSerializerContext;
