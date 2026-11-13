using System.Text;
using System.Text.Json;

namespace Dockyard.Agent;

sealed class AgentApi(DomainTable table, DomainProxy proxy)
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
                case "tls" when request is { Cert: { } cert, Key: { } key }:
                    proxy.SetCertificate(cert, key);
                    return new AgentReply();
                default: return new AgentReply { Error = "unknown request" };
            }
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return new AgentReply { Error = e.Message };
        }
    }
}
