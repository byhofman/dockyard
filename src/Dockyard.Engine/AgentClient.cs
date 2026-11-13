using System.Text;
using System.Text.Json;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

// The part of Dockyard that lives next to the engine. Not every engine has one: the Windows engine does not,
// and neither does whatever else a docker context may point at.
public sealed class AgentClient(DockerEndpoint endpoint)
{
    public static AgentClient? For(DockerEndpoint engine)
    {
        if (engine.Kind == EndpointKind.NamedPipe && engine.Address == "dockyard_linux")
            return new AgentClient(DockerEndpoint.Parse("npipe:////./pipe/dockyard_linux_agent"));
        if (engine.Kind != EndpointKind.Unix) return null;
        if (engine.Address is "/var/run/docker.sock" or "/run/docker.sock")
            return new AgentClient(DockerEndpoint.Parse("unix:///run/dockyard/agent.sock"));
        if (engine.Address.EndsWith("/.dockyard/docker.sock", StringComparison.Ordinal))
            return new AgentClient(DockerEndpoint.Parse("unix://" + Path.Combine(Path.GetDirectoryName(engine.Address)!, "run", "agent.sock")));
        return null;
    }

    async Task<AgentMessage> Ask(AgentMessage request, CancellationToken ct)
    {
        await using var stream = await Connect(ct);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(stream, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, AgentClientJson.Default.AgentMessage));
        var line = await reader.ReadLineAsync(ct) ?? throw new IOException("The agent closed the connection");
        var reply = JsonSerializer.Deserialize(line, AgentClientJson.Default.AgentMessage) ?? throw new IOException("The agent sent nothing usable");
        return reply.Error is { } error ? throw new InvalidOperationException(error) : reply;
    }

    // A pipe nobody serves, as when no tray runs on Windows, times out instead of refusing; callers treat both alike.
    async Task<Stream> Connect(CancellationToken ct)
    {
        try { return await DockerTransport.Connect(endpoint, ct); }
        catch (TimeoutException e) { throw new IOException("The agent next to the engine did not answer", e); }
    }

    static DomainList List(AgentMessage reply) => new(reply.Domains ?? [], reply.Https);

    public async Task<DomainList> DomainsAsync(CancellationToken ct = default) =>
        List(await Ask(new AgentMessage { Op = "domains" }, ct));

    public async Task<DomainList> ChoosePortAsync(string container, int? port, CancellationToken ct = default) =>
        List(await Ask(new AgentMessage { Op = "choose-port", Container = container, Port = port }, ct));

    public Task SetCertificateAsync(string certPem, string keyPem, CancellationToken ct = default) =>
        Ask(new AgentMessage { Op = "tls", Cert = certPem, Key = keyPem }, ct);
}
