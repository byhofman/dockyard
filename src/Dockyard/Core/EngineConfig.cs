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

    public static bool NamesTheAgent(string? existing) => HasOurDns(Parse(existing));

    public static bool DnsIsSomeoneElses(string? existing)
    {
        var config = Parse(existing);
        return config["dns"] is JsonArray { Count: > 0 } && !HasOurDns(config);
    }

    // DNS servers someone else put in daemon.json, in resolv.conf form: the agent takes their place in the file and asks
    // them for every name that is not Dockyard's, so containers resolve as before and also get Dockyard's names.
    public static string? KeptDns(string? existing)
    {
        var servers = Parse(existing)["dns"] is JsonArray dns
            ? dns.Select(n => n?.GetValue<string>()).OfType<string>().Where(s => s != DnsAddress).ToList()
            : [];
        return servers.Count == 0 ? null : string.Concat(servers.Select(s => $"nameserver {s}\n"));
    }

    static IEnumerable<string> KeptServers(string kept) => kept.Split('\n')
        .Select(l => l.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        .Where(p => p.Length >= 2 && p[0] == "nameserver")
        .Select(p => p[1]);

    public static string WithDockyard(string? existing, ProxySettings? proxy = null)
    {
        var config = Parse(existing);
        config["dns"] = new JsonArray(DnsAddress);
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

    public static string WithoutDockyard(string? existing, string? kept)
    {
        var config = Parse(existing);
        if (!HasOurDns(config)) return config.ToJsonString(Indented);
        var servers = kept is null ? [] : KeptServers(kept).ToList();
        if (servers.Count == 0) config.Remove("dns");
        else config["dns"] = new JsonArray(servers.Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
        return config.ToJsonString(Indented);
    }

    // The file for an engine Dockyard owns, on macOS and in WSL: rendered fresh, nobody else writes it.
    public static string Render(ProxySettings? proxy, Settings? settings = null)
    {
        var config = new JsonObject { ["dns"] = new JsonArray(DnsAddress) };
        static JsonArray Strings(IEnumerable<string> items) => new(items.Select(i => (JsonNode)JsonValue.Create(i)).ToArray());
        var mirrors = EngineOptions.Lines(settings?.RegistryMirrors).ToList();
        if (mirrors.Count > 0) config["registry-mirrors"] = Strings(mirrors);
        var insecure = EngineOptions.Lines(settings?.InsecureRegistries).ToList();
        if (insecure.Count > 0) config["insecure-registries"] = Strings(insecure);
        var pools = EngineOptions.Lines(settings?.AddressPools).Select(l => EngineOptions.ParsePool(l).Pool).OfType<AddressPool>().ToList();
        if (pools.Count > 0)
            config["default-address-pools"] = new JsonArray(pools.Select(p => (JsonNode)new JsonObject { ["base"] = p.Base, ["size"] = p.Size }).ToArray());
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
        string? updated;
        // A file docker itself cannot read is not this setup's to fail on or to overwrite.
        try { updated = WithClientProxy(existing, endpoint, proxy); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { return; }
        if (updated is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".dockyard";
        await File.WriteAllTextAsync(temp, updated);
        File.Move(temp, file, overwrite: true);
    }
}
