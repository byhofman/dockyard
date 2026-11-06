using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed class DomainInfo
{
    public string ContainerId { get; set; } = "";
    public string Domain { get; set; } = "";
    public int? Port { get; set; }
    public bool PortIsChosen { get; set; }
}

sealed class AgentMessage
{
    public string? Op { get; set; }
    public string? Container { get; set; }
    public int? Port { get; set; }
    public string? Cert { get; set; }
    public string? Key { get; set; }
    public string? Error { get; set; }
    public List<DomainInfo>? Domains { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentMessage))]
partial class AgentClientJson : JsonSerializerContext;

// The part of Dockyard that lives next to the engine. Not every engine has one: the Windows engine does not,
// and neither does whatever else a docker context may point at.
public sealed class AgentClient(DockerEndpoint endpoint)
{
    public const int WslApiPort = 2379;

    public static AgentClient? For(DockerEndpoint engine)
    {
        if (engine.Kind == EndpointKind.Tcp && engine.Address is "127.0.0.1" or "localhost" && engine.Port == 2375)
            return new AgentClient(new DockerEndpoint(EndpointKind.Tcp, "127.0.0.1", WslApiPort, $"tcp://127.0.0.1:{WslApiPort}"));
        if (engine.Kind != EndpointKind.Unix) return null;
        if (engine.Address is "/var/run/docker.sock" or "/run/docker.sock")
            return new AgentClient(DockerEndpoint.Parse("unix:///run/dockyard/agent.sock"));
        if (engine.Address.EndsWith("/.dockyard/docker.sock", StringComparison.Ordinal))
            return new AgentClient(DockerEndpoint.Parse("unix://" + Path.Combine(Path.GetDirectoryName(engine.Address)!, "run", "agent.sock")));
        return null;
    }

    async Task<AgentMessage> Ask(AgentMessage request, CancellationToken ct)
    {
        await using var stream = await DockerTransport.Connect(endpoint, ct);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(stream, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AgentClientJson.Default.AgentMessage));
        var line = await reader.ReadLineAsync(ct) ?? throw new IOException("The agent closed the connection");
        var reply = JsonSerializer.Deserialize(line, AgentClientJson.Default.AgentMessage) ?? throw new IOException("The agent sent nothing usable");
        return reply.Error is { } error ? throw new InvalidOperationException(error) : reply;
    }

    public async Task<List<DomainInfo>> DomainsAsync(CancellationToken ct = default) =>
        (await Ask(new AgentMessage { Op = "domains" }, ct)).Domains ?? [];

    public async Task<List<DomainInfo>> ChoosePortAsync(string container, int? port, CancellationToken ct = default) =>
        (await Ask(new AgentMessage { Op = "choose-port", Container = container, Port = port }, ct)).Domains ?? [];

    public Task SetCertificateAsync(string certPem, string keyPem, CancellationToken ct = default) =>
        Ask(new AgentMessage { Op = "tls", Cert = certPem, Key = keyPem }, ct);
}
