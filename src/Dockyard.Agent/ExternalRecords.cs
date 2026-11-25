using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Dockyard.Agent;

// A name a kind cluster's external-dns published: an Ingress host or an annotated LoadBalancer service.
sealed record ExternalRecord(string Cluster, string Name, IPAddress Address);

// What external-dns in each cluster writes to dockyard-etcd, the CoreDNS provider's layout under a prefix per cluster:
// /dockyard/<cluster>/internal/dockyard/<host>/<id> holding {"host": "<address>", ...}.
static class ExternalRecords
{
    public const string Prefix = "/dockyard/";
    public static string Endpoint => $"http://{GuestAddresses.Dns}:{GuestAddresses.EtcdPort}";

    public static string RangeRequest() => new JsonObject
    {
        ["key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Prefix)),
        // The key after every one that starts with the prefix: its last byte plus one.
        ["range_end"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Prefix[..^1] + (char)(Prefix[^1] + 1))),
    }.ToJsonString();

    public static List<ExternalRecord> Parse(string response)
    {
        List<ExternalRecord> records = [];
        if (JsonNode.Parse(response)?["kvs"] is not JsonArray kvs) return records;
        foreach (var kv in kvs)
        {
            try
            {
                var key = Encoding.UTF8.GetString(Convert.FromBase64String(kv?["key"]?.GetValue<string>() ?? ""));
                var value = Encoding.UTF8.GetString(Convert.FromBase64String(kv?["value"]?.GetValue<string>() ?? ""));
                var parts = key[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                var name = string.Join('.', parts[1..^1].Reverse()).ToLowerInvariant();
                if (!DomainNames.InZone(name) || name == DomainNames.Zone) continue;
                if (JsonNode.Parse(value)?["host"]?.GetValue<string>() is not { } host || !IPAddress.TryParse(host, out var address)) continue;
                records.Add(new ExternalRecord(parts[0], name, address));
            }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException) { }
        }
        return records;
    }

    public static async Task<List<ExternalRecord>> Read(HttpClient http, CancellationToken ct)
    {
        using var content = new StringContent(RangeRequest(), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{Endpoint}/v3/kv/range", content, ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
