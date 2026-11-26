using System.Text.Json.Nodes;

namespace Dockyard.Engine;

// A container Dockyard keeps running next to kind's clusters, on kind's network. SetupLabel, when there is one, names the
// label whose value says how it was made: a container with another value is made again.
public sealed record ServiceContainerSpec(
    string Name,
    string Image,
    IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<string> Binds,
    IReadOnlyList<int> LoopbackPorts,
    bool Privileged = false,
    string? SetupLabel = null)
{
    public const string Network = "kind";

    public string? Setup => SetupLabel is { } key ? Labels.GetValueOrDefault(key) : null;

    public JsonObject CreateBody()
    {
        var exposed = new JsonObject();
        var bindings = new JsonObject();
        foreach (var port in LoopbackPorts)
        {
            exposed[$"{port}/tcp"] = new JsonObject();
            bindings[$"{port}/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "127.0.0.1", ["HostPort"] = port.ToString() });
        }
        var labels = new JsonObject();
        foreach (var (key, value) in Labels) labels[key] = value;
        return new JsonObject
        {
            ["Image"] = Image,
            ["Cmd"] = new JsonArray(Command.Select(c => (JsonNode)JsonValue.Create(c)).ToArray()),
            ["Labels"] = labels,
            ["ExposedPorts"] = exposed,
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = Network,
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" },
                ["Binds"] = new JsonArray(Binds.Select(b => (JsonNode)JsonValue.Create(b)).ToArray()),
                ["PortBindings"] = bindings,
                ["Privileged"] = Privileged,
            },
        };
    }
}
