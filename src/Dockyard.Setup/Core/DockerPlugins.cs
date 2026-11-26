using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

// A package manager puts docker-compose and docker-buildx in a folder of its own, where the docker CLI only looks when
// ~/.docker/config.json names it.
static class DockerPlugins
{
    public static string? WithExtraDir(string? json, string dir)
    {
        JsonObject config;
        try { config = string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json) as JsonObject ?? []; }
        catch (JsonException) { return null; }
        var dirs = config["cliPluginsExtraDirs"] as JsonArray ?? [];
        if (dirs.Any(d => string.Equals(d?.GetValue<string>(), dir, StringComparison.OrdinalIgnoreCase))) return null;
        dirs.Add((JsonNode?)JsonValue.Create(dir));
        config["cliPluginsExtraDirs"] = dirs;
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task AddExtraDir(Action<string> log, string dir)
    {
        var file = CredentialStore.ConfigFile;
        var existing = File.Exists(file) ? await File.ReadAllTextAsync(file) : null;
        if (WithExtraDir(existing, dir) is not { } updated)
        {
            if (existing is not null && !IsJson(existing)) log($"{file} is not valid JSON, so docker may not find the plugins in {dir}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".dockyard";
        await File.WriteAllTextAsync(temp, updated);
        File.Move(temp, file, overwrite: true);
        log($"docker finds its plugins in {dir} too");
    }

    static bool IsJson(string text)
    {
        try { JsonNode.Parse(text); return true; }
        catch (JsonException) { return false; }
    }
}
