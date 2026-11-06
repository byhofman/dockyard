using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

// The engine's daemon.json. Keys Dockyard does not manage stay as they are where someone else may have written them,
// which only happens on a Linux host: there the file belongs to the system's Docker.
static class EngineConfig
{
    public const string DnsAddress = "198.19.250.1";
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    static JsonObject Parse(string? json)
    {
        try { return JsonNode.Parse(json ?? "{}") as JsonObject ?? []; }
        catch (JsonException) { throw new InvalidOperationException("daemon.json is not valid JSON; it was left as it is"); }
    }

    static bool HasOurDns(JsonObject config) =>
        config["dns"] is JsonArray { Count: 1 } dns && dns[0]?.GetValue<string>() == DnsAddress;

    public static bool DnsIsSomeoneElses(string? existing)
    {
        var config = Parse(existing);
        return config["dns"] is JsonArray { Count: > 0 } && !HasOurDns(config);
    }

    // DNS someone configured is theirs: then containers do without Dockyard's names, and the wizard says so.
    public static string WithDockyard(string? existing, ProxySettings? proxy = null)
    {
        var config = Parse(existing);
        if (!DnsIsSomeoneElses(existing)) config["dns"] = new JsonArray(DnsAddress);
        // Only into a file with no proxies of its own, or back out of one this code wrote.
        if (config["proxies"] is null or JsonObject { Count: > 0 } && (config["proxies"] is null || IsOurs(config["proxies"]!)))
        {
            config.Remove("proxies");
            if (proxy is { IsEmpty: false } && JsonNode.Parse(Render(proxy))!["proxies"] is { } rendered) config["proxies"] = rendered.DeepClone();
        }
        return config.ToJsonString(Indented);
    }

    // A loopback proxy goes through the agent's address, and every no-proxy list here starts with the same entries.
    static bool IsOurs(JsonNode proxies) => proxies["no-proxy"]?.GetValue<string>().StartsWith("localhost,127.0.0.1,::1,host.docker.internal", StringComparison.Ordinal) == true;

    public static string WithoutDockyard(string? existing)
    {
        var config = Parse(existing);
        if (HasOurDns(config)) config.Remove("dns");
        return config.ToJsonString(Indented);
    }

    // The file for an engine Dockyard owns, on macOS and in WSL: rendered fresh, nobody else writes it.
    public static string Render(ProxySettings? proxy)
    {
        var config = new JsonObject { ["dns"] = new JsonArray(DnsAddress) };
        if (proxy is { IsEmpty: false })
        {
            var engine = HostProxySettings.ForEngine(proxy);
            var proxies = new JsonObject { ["no-proxy"] = string.Join(',', engine.NoProxy) };
            if (engine.Http is not null) proxies["http-proxy"] = engine.Http;
            if (engine.Https is not null) proxies["https-proxy"] = engine.Https;
            config["proxies"] = proxies;
        }
        return config.ToJsonString(Indented);
    }

    // The CLI passes these on to containers and builds. Keyed by endpoint so another Docker app's engine on this machine
    // sees no change; a "default" entry here would apply to every context.
    public static string? WithClientProxy(string? configJson, string endpoint, ProxySettings? proxy)
    {
        var config = Parse(configJson);
        var entry = config["proxies"]?[endpoint];
        if (proxy is not { IsEmpty: false })
        {
            if (entry is null) return null;
            ((JsonObject)config["proxies"]!).Remove(endpoint);
            if (config["proxies"] is JsonObject { Count: 0 }) config.Remove("proxies");
            return config.ToJsonString(Indented);
        }
        var engine = HostProxySettings.ForEngine(proxy);
        var wanted = new JsonObject { ["noProxy"] = string.Join(',', engine.NoProxy) };
        if (engine.Http is not null) wanted["httpProxy"] = engine.Http;
        if (engine.Https is not null) wanted["httpsProxy"] = engine.Https;
        if (entry is not null && JsonNode.DeepEquals(entry, wanted)) return null;
        if (config["proxies"] is not JsonObject proxies) config["proxies"] = proxies = [];
        proxies[endpoint] = wanted;
        return config.ToJsonString(Indented);
    }

    public static async Task ApplyClientProxy(string endpoint, ProxySettings? proxy)
    {
        var file = CredentialStore.ConfigFile;
        var existing = File.Exists(file) ? await File.ReadAllTextAsync(file) : null;
        if (WithClientProxy(existing, endpoint, proxy) is not { } updated) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".dockyard";
        await File.WriteAllTextAsync(temp, updated);
        File.Move(temp, file, overwrite: true);
    }
}

static class GuestBundle
{
    public const string ResourceName = "guest.tar.gz";

    // The agent out of the bundle Dockyard carries; null for a build without one.
    public static async Task<string?> ExtractAgent(string directory)
    {
        await using var resource = typeof(GuestBundle).Assembly.GetManifestResourceStream(ResourceName);
        if (resource is null) return null;
        await using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            if (!entry.Name.EndsWith("/dockyard-agent", StringComparison.Ordinal) || entry.DataStream is null) continue;
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "dockyard-agent");
            await using var file = File.Create(path);
            await entry.DataStream.CopyToAsync(file);
            return path;
        }
        return null;
    }
}
