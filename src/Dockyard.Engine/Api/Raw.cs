using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Dockyard.Engine;

// Whole API objects that have to survive untouched from one engine to another, and tar streams.
public sealed partial class DockerClient
{
    public async Task<JsonNode> GetNodeAsync(string path, CancellationToken ct = default)
    {
        using var res = await Send(HttpMethod.Get, path, null, stream: false, ct);
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))!;
    }

    public async Task<JsonNode?> PostNodeAsync(string path, JsonNode? body, CancellationToken ct = default)
    {
        using var content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await Send(HttpMethod.Post, path, content, stream: false, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        return text.Length == 0 ? null : JsonNode.Parse(text);
    }

    // The caller disposes the response when done reading; nothing is buffered, an image or a volume can be large.
    public Task<HttpResponseMessage> OpenAsync(string path, CancellationToken ct = default) =>
        Send(HttpMethod.Get, path, null, stream: true, ct);

    public async Task SendTarAsync(HttpMethod method, string path, Stream tar, CancellationToken ct = default)
    {
        using var content = new StreamContent(tar, 64 * 1024);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
        using var res = await Send(method, path, content, stream: true, ct);
        // images/load reports a failure inside a 200 response.
        await using var reply = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(reply);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (line.Contains("\"error\"", StringComparison.Ordinal))
                throw new DockerException(System.Net.HttpStatusCode.InternalServerError, JsonNode.Parse(line)?["error"]?.GetValue<string>() ?? line);
    }

    public Task<HttpResponseMessage> GetArchiveAsync(string container, string path, CancellationToken ct = default) =>
        OpenAsync($"containers/{Esc(container)}/archive?path={Esc(path)}", ct);

    public Task PutArchiveAsync(string container, string directory, Stream tar, CancellationToken ct = default) =>
        SendTarAsync(HttpMethod.Put, $"containers/{Esc(container)}/archive?path={Esc(directory)}", tar, ct);

    // An image with nothing in it: enough to create a container that is never started and only holds a volume mount
    // for the archive API, without pulling anything or depending on what images the engine has.
    public async Task<string> EnsureEmptyImageAsync(CancellationToken ct = default)
    {
        const string name = "dockyard-empty:1";
        // One empty file, not an empty archive: an engine on the containerd image store reports success for that and makes no image.
        var tar = new MemoryStream();
        using (var writer = new System.Formats.Tar.TarWriter(tar, System.Formats.Tar.TarEntryFormat.Ustar, leaveOpen: true))
            writer.WriteEntry(new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.RegularFile, ".dockyard-empty"));
        tar.Position = 0;
        await SendTarAsync(HttpMethod.Post, "images/create?fromSrc=-&repo=dockyard-empty&tag=1", tar, ct);
        return name;
    }

    public async Task<string> CreateVolumeHolderAsync(string image, string volume, string mountPoint, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["Image"] = image,
            ["Cmd"] = new JsonArray("/none"),
            ["Labels"] = new JsonObject { ["dockyard.helper"] = "1" },
            ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray($"{volume}:{mountPoint}") },
        };
        return (await PostNodeAsync("containers/create", body, ct))!["Id"]!.GetValue<string>();
    }
}
