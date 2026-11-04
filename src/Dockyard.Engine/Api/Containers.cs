using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<List<ContainerSummary>> ListContainersAsync(bool all = true, CancellationToken ct = default) =>
        GetJson($"containers/json?all={Bool(all)}", ContainerJson.Default.ListContainerSummary, ct);

    public Task<ContainerInspect> InspectContainerAsync(string id, CancellationToken ct = default) =>
        GetJson($"containers/{Esc(id)}/json", ContainerJson.Default.ContainerInspect, ct);

    public async Task<string> CreateContainerAsync(CreateContainerRequest request, string? name = null, CancellationToken ct = default)
    {
        var path = "containers/create" + (name is null ? "" : $"?name={Esc(name)}");
        var res = await SendJson(HttpMethod.Post, path, Body(request, ContainerJson.Default.CreateContainerRequest),
            ContainerJson.Default.CreateContainerResponse, ct);
        return res.Id;
    }

    public Task StartContainerAsync(string id, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"containers/{Esc(id)}/start", null, ct);

    public Task StopContainerAsync(string id, int? timeoutSeconds = null, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"containers/{Esc(id)}/stop" + StopTimeout(timeoutSeconds), null, ct);

    public Task RestartContainerAsync(string id, int? timeoutSeconds = null, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"containers/{Esc(id)}/restart" + StopTimeout(timeoutSeconds), null, ct);

    public Task RemoveContainerAsync(string id, bool force = false, bool removeVolumes = false, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Delete, $"containers/{Esc(id)}?force={Bool(force)}&v={Bool(removeVolumes)}", null, ct);

    static string StopTimeout(int? seconds) => seconds is { } t ? $"?t={t}" : "";
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ContainerSummary>))]
[JsonSerializable(typeof(ContainerInspect))]
[JsonSerializable(typeof(CreateContainerRequest))]
[JsonSerializable(typeof(CreateContainerResponse))]
partial class ContainerJson : JsonSerializerContext;
