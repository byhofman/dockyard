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
    public static string WithDockyard(string? existing)
    {
        var config = Parse(existing);
        if (!DnsIsSomeoneElses(existing)) config["dns"] = new JsonArray(DnsAddress);
        return config.ToJsonString(Indented);
    }

    public static string WithoutDockyard(string? existing)
    {
        var config = Parse(existing);
        if (HasOurDns(config)) config.Remove("dns");
        return config.ToJsonString(Indented);
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
