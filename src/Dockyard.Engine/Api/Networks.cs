namespace Dockyard.Engine;

public sealed partial class DockerClient
{
    public Task<List<NetworkSummary>> ListNetworksAsync(CancellationToken ct = default) =>
        GetJson("networks", NetworkJson.Default.ListNetworkSummary, ct);

    public Task<NetworkSummary> InspectNetworkAsync(string id, CancellationToken ct = default) =>
        GetJson($"networks/{Esc(id)}", NetworkJson.Default.NetworkSummary, ct);

    public async Task<string> CreateNetworkAsync(string name, string driver = "bridge", CancellationToken ct = default)
    {
        var body = Body(new NetworkCreateRequest { Name = name, Driver = driver }, NetworkJson.Default.NetworkCreateRequest);
        return (await SendJson(HttpMethod.Post, "networks/create", body, NetworkJson.Default.NetworkCreateResponse, ct)).Id;
    }

    public Task RemoveNetworkAsync(string id, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Delete, $"networks/{Esc(id)}", null, ct);

    public Task ConnectNetworkAsync(string network, string container, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"networks/{Esc(network)}/connect",
            Body(new NetworkConnectRequest { Container = container }, NetworkJson.Default.NetworkConnectRequest), ct);

    public Task DisconnectNetworkAsync(string network, string container, bool force = false, CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, $"networks/{Esc(network)}/disconnect",
            Body(new NetworkDisconnectRequest { Container = container, Force = force }, NetworkJson.Default.NetworkDisconnectRequest), ct);

    public Task PruneNetworksAsync(CancellationToken ct = default) =>
        SendNoContent(HttpMethod.Post, "networks/prune", null, ct);
}
