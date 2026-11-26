using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

static class DaemonJson
{
    const string Group = "docker-users";

    // dockerd resolves each comma-separated name in "group" when it creates the pipe, so naming the user gives access
    // right away, while group membership only applies after signing out. Other settings and users stay as they are.
    public static string WithAccessFor(string? existing, string host, string? user)
    {
        JsonObject config;
        try { config = JsonNode.Parse(existing ?? "{}") as JsonObject ?? []; }
        catch (JsonException) { config = []; }

        var names = (config["group"]?.GetValue<string>() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var name in new[] { Group, user })
            if (name is not null && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);

        config["hosts"] = new JsonArray(host);
        config["group"] = string.Join(',', names);
        return config.ToJsonString();
    }
}
