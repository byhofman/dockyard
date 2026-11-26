using System.Text;
using System.Text.Json;

namespace Dockyard.Agent;

sealed class AgentApi(DomainTable table, DomainProxy proxy, Services? services = null, ClusterServices? clusters = null)
{
    public async Task Handle(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        while (await Lines.Read(reader, ct) is { } line)
            await writer.WriteLineAsync(JsonSerializer.Serialize(Reply(line), AgentApiJson.Default.AgentReply));
    }

    public AgentReply Reply(string line)
    {
        try
        {
            var request = JsonSerializer.Deserialize(line, AgentApiJson.Default.AgentRequest);
            switch (request?.Op)
            {
                case "domains": return new AgentReply { Domains = table.List(), Https = proxy.HasCertificate };
                case "choose-port" when request is { Container: { } container }:
                    table.Choose(container, request.Port);
                    return new AgentReply { Domains = table.List(), Https = proxy.HasCertificate };
                case "names" when request is { On: { } on }:
                    table.Enabled = on;
                    services?.NamesChanged();
                    return new AgentReply();
                case "tls" when request is { Cert: { } cert, Key: { } key }:
                    proxy.SetCertificate(cert, key);
                    services?.SetCertificate(cert, key);
                    return new AgentReply();
                case "cluster-wants" when request is { Wants: { } wants } && clusters is not null:
                    clusters.SetWants(wants);
                    return new AgentReply();
                case "cluster-status" when clusters is not null:
                    var status = clusters.Status();
                    return new AgentReply { Clusters = [.. status.Clusters], ClusterErrors = [.. status.Errors] };
                default: return new AgentReply { Error = "unknown request" };
            }
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return new AgentReply { Error = e.Message };
        }
    }
}
