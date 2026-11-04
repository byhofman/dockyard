using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed record PingResult(string ApiVersion, string OsType);

public sealed class VersionInfo
{
    public string Version { get; set; } = "";
    public string ApiVersion { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
}

public sealed class PruneResult
{
    public long SpaceReclaimed { get; set; }
}

sealed class ErrorResponse
{
    public string? Message { get; set; }
}

public sealed partial class DockerClient : IDisposable
{
    public static readonly Version MinApiVersion = new(1, 44);

    readonly HttpClient http;
    string? apiPrefix;

    public DockerClient(DockerEndpoint endpoint) : this(endpoint, DockerTransport.CreateHandler(endpoint)) { }

    internal DockerClient(DockerEndpoint endpoint, HttpMessageHandler handler)
    {
        Endpoint = endpoint;
        http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    public static DockerClient FromActiveContext()
    {
        var context = DockerContext.Resolve();
        return new DockerClient(context.Endpoint) { Context = context };
    }

    public ResolvedContext? Context { get; private init; }
    public DockerEndpoint Endpoint { get; }
    public string? ApiVersion { get; private set; }
    public string OsType { get; private set; } = "linux";
    public bool IsWindows => OsType == "windows";

    public async Task<PingResult> PingAsync(CancellationToken ct = default)
    {
        HttpResponseMessage res;
        try { res = await http.GetAsync("_ping", ct); }
        catch (HttpRequestException ex) { throw new EngineUnavailableException(Endpoint, ex); }

        using (res)
        {
            await EnsureSuccess(res, ct);
            var version = Header(res, "Api-Version")
                ?? throw new DockerException(res.StatusCode, "The engine did not report an API version");
            if (Version.Parse(version) < MinApiVersion)
                throw new DockerException(res.StatusCode, $"Docker engine API {version} is older than the minimum {MinApiVersion}");

            OsType = Header(res, "Ostype")?.ToLowerInvariant() ?? "linux";
            ApiVersion = version;
            apiPrefix = $"v{version}/";
            return new PingResult(version, OsType);
        }
    }

    public Task<VersionInfo> VersionAsync(CancellationToken ct = default) => GetJson("version", CoreJson.Default.VersionInfo, ct);

    internal async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? body, bool stream, CancellationToken ct)
    {
        if (apiPrefix is null) await PingAsync(ct);
        var request = new HttpRequestMessage(method, apiPrefix + path) { Content = body };
        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(request, stream ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
        }
        catch (HttpRequestException ex) { throw new EngineUnavailableException(Endpoint, ex); }
        await EnsureSuccess(res, ct);
        return res;
    }

    internal Task<T> GetJson<T>(string path, JsonTypeInfo<T> type, CancellationToken ct) => SendJson(HttpMethod.Get, path, null, type, ct);

    internal async Task<T> SendJson<T>(HttpMethod method, string path, HttpContent? body, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var res = await Send(method, path, body, stream: false, ct);
        await using var s = await res.Content.ReadAsStreamAsync(ct);
        return (await JsonSerializer.DeserializeAsync(s, type, ct))!;
    }

    internal async Task SendNoContent(HttpMethod method, string path, HttpContent? body, CancellationToken ct)
    {
        using var _ = await Send(method, path, body, stream: false, ct);
    }

    internal static HttpContent Body<T>(T value, JsonTypeInfo<T> type) => JsonContent.Create(value, type);

    internal static string Esc(string value) => Uri.EscapeDataString(value);

    internal static string Bool(bool value) => value ? "1" : "0";

    internal static string Filters(params (string Key, string Value)[] filters)
    {
        var map = new Dictionary<string, string[]>();
        foreach (var group in filters.GroupBy(f => f.Key))
            map[group.Key] = group.Select(f => f.Value).ToArray();
        return Esc(JsonSerializer.Serialize(map, CoreJson.Default.DictionaryStringStringArray));
    }

    static string? Header(HttpResponseMessage res, string name) =>
        res.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    static async Task EnsureSuccess(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.NotModified) return;

        var body = await res.Content.ReadAsStringAsync(ct);
        string? message;
        try { message = JsonSerializer.Deserialize(body, CoreJson.Default.ErrorResponse)?.Message; }
        catch (JsonException) { message = body; }
        if (string.IsNullOrWhiteSpace(message)) message = res.ReasonPhrase ?? res.StatusCode.ToString();
        message = message.Trim();

        var status = res.StatusCode;
        res.Dispose();
        throw status switch
        {
            HttpStatusCode.NotFound => new NotFoundException(message),
            HttpStatusCode.Conflict => new ConflictException(message),
            _ => new DockerException(status, message),
        };
    }

    public void Dispose() => http.Dispose();
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(PruneResult))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
partial class CoreJson : JsonSerializerContext;
