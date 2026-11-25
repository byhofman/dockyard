using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockyard.Core;

// The daemon.json settings people change most, with what each takes: enough to explain them, check their type while they
// are typed and add them in one go. dockerd itself has the final word, so a key not described here is only a note.
static class DaemonJsonSchema
{
    public enum Type { String, Bool, Number, StringList, Object, ObjectList }

    public sealed record Key(string Name, Type Type, string Description, string Example, bool Windows = true);

    public static readonly IReadOnlyList<Key> Keys =
    [
        new("registry-mirrors", Type.StringList, "Pull images from Docker Hub through these first: a company cache, or one that avoids its rate limits.", "[\"https://mirror.gcr.io\"]"),
        new("insecure-registries", Type.StringList, "Registries reached over plain HTTP or with an untrusted certificate, such as one on localhost.", "[\"localhost:5000\"]"),
        new("default-address-pools", Type.ObjectList, "The ranges new networks get their addresses from, for when the defaults clash with a VPN.", "[{ \"base\": \"10.20.0.0/16\", \"size\": 24 }]", Windows: false),
        new("bip", Type.String, "The address of the default bridge network, docker0.", "\"10.30.0.1/24\"", Windows: false),
        new("log-driver", Type.String, "Where containers' logs go by default.", "\"json-file\"", Windows: false),
        new("log-opts", Type.Object, "Options for the log driver, such as rotation for json-file.", "{ \"max-size\": \"10m\", \"max-file\": \"3\" }", Windows: false),
        new("log-level", Type.String, "How much the engine itself logs: debug, info, warn, error or fatal.", "\"warn\""),
        new("debug", Type.Bool, "The engine logs at debug level.", "true"),
        new("features", Type.Object, "Engine features to turn on or off, such as the containerd image store.", "{ \"containerd-snapshotter\": true }", Windows: false),
        new("builder", Type.Object, "BuildKit's own settings, such as garbage collection of the build cache.", "{ \"gc\": { \"enabled\": true, \"defaultKeepStorage\": \"20GB\" } }", Windows: false),
        new("max-concurrent-downloads", Type.Number, "Layers pulled at once per image (3 by default).", "6"),
        new("max-concurrent-uploads", Type.Number, "Layers pushed at once per image (5 by default).", "5"),
        new("max-download-attempts", Type.Number, "Tries per layer before a pull fails (5 by default).", "5"),
        new("live-restore", Type.Bool, "Containers keep running while the engine itself restarts.", "true", Windows: false),
        new("mtu", Type.Number, "The packet size of the default bridge network, lower behind some VPNs.", "1400", Windows: false),
        new("ipv6", Type.Bool, "IPv6 on the default bridge network.", "true", Windows: false),
        new("default-ulimits", Type.Object, "Limits containers get unless they set their own, such as open files.", "{ \"nofile\": { \"Name\": \"nofile\", \"Hard\": 65536, \"Soft\": 65536 } }", Windows: false),
        new("shutdown-timeout", Type.Number, "Seconds the engine gives containers to stop when it stops.", "15"),
        new("experimental", Type.Bool, "The engine's experimental features.", "true"),
        new("dns", Type.StringList, "DNS servers for containers. Dockyard sets this to its agent, which needs it for container domains.", "[\"1.1.1.1\"]", Windows: false),
        new("proxies", Type.Object, "The proxy the engine pulls through. Dockyard writes this computer's when there is none here.", "{ \"http-proxy\": \"http://proxy:3128\", \"https-proxy\": \"http://proxy:3128\", \"no-proxy\": \"localhost,127.0.0.1\" }"),
        new("hosts", Type.StringList, "Where the engine listens. Dockyard keeps the Windows engine on its own pipe.", "[\"npipe:////./pipe/docker_windows\"]"),
        new("group", Type.String, "Who may use the Windows engine's pipe. Dockyard adds you and docker-users.", "\"docker-users\""),
    ];

    public static IReadOnlyList<Key> For(string engine) =>
        Keys.Where(k => engine == EngineSettings.Windows ? k.Windows && k.Name != "dns" : k.Name is not ("hosts" or "group")).ToList();

    public enum Severity { Error, Note }

    public sealed record Issue(int Line, string Message, Severity Severity);

    // Line numbers count from 1; an issue without a place in the text is on line 0.
    public static IReadOnlyList<Issue> Check(string engine, string json)
    {
        List<Issue> issues = [];
        if (json.Trim().Length == 0) return issues;
        var bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
        Dictionary<string, int> lines = [];
        try
        {
            while (reader.Read())
                if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName)
                    lines[reader.GetString()!] = LineAt(bytes, (int)reader.TokenStartIndex);
        }
        catch (JsonException e)
        {
            issues.Add(new((int)(e.LineNumber ?? 0) + 1, e.Message.Split(". ")[0].TrimEnd('.'), Severity.Error));
            return issues;
        }
        if (JsonNode.Parse(json) is not JsonObject config)
        {
            issues.Add(new(1, "daemon.json has to be one JSON object, in { }", Severity.Error));
            return issues;
        }
        var known = For(engine).ToDictionary(k => k.Name);
        foreach (var (name, value) in config)
        {
            var line = lines.GetValueOrDefault(name);
            if (!known.TryGetValue(name, out var key))
            {
                issues.Add(new(line, $"\"{name}\" is not one of the common settings; dockerd's check below says whether it knows it", Severity.Note));
                continue;
            }
            if (!Fits(key.Type, value)) issues.Add(new(line, $"\"{name}\" takes {Describe(key.Type)}, like {key.Example}", Severity.Error));
            if (EngineSettings.DockyardKeys(engine).Contains(name)) issues.Add(new(line, $"Dockyard sets \"{name}\" itself, so yours is replaced", Severity.Note));
        }
        return issues;
    }

    static int LineAt(byte[] bytes, int offset)
    {
        var line = 1;
        for (var i = 0; i < offset && i < bytes.Length; i++) if (bytes[i] == '\n') line++;
        return line;
    }

    static bool Fits(Type type, JsonNode? value) => type switch
    {
        Type.String => value is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        Type.Bool => value is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        Type.Number => value is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        Type.StringList => value is JsonArray a && a.All(i => i is JsonValue s && s.GetValueKind() == JsonValueKind.String),
        Type.Object => value is JsonObject,
        Type.ObjectList => value is JsonArray a && a.All(i => i is JsonObject),
        _ => true,
    };

    static string Describe(Type type) => type switch
    {
        Type.String => "text",
        Type.Bool => "true or false",
        Type.Number => "a number",
        Type.StringList => "a list of texts",
        Type.Object => "an object",
        _ => "a list of objects",
    };

    // What the Add menu offers, each merged into what is there: lists get the item added, objects the keys.
    public sealed record Snippet(string Title, string Json);

    public static IReadOnlyList<Snippet> Snippets(string engine) => engine == EngineSettings.Windows
        ?
        [
            new("Registry mirror", """{ "registry-mirrors": ["https://mirror.gcr.io"] }"""),
            new("Insecure registry", """{ "insecure-registries": ["localhost:5000"] }"""),
            new("Debug logging", """{ "debug": true, "log-level": "debug" }"""),
            new("More parallel downloads", """{ "max-concurrent-downloads": 6 }"""),
        ]
        :
        [
            new("Registry mirror", """{ "registry-mirrors": ["https://mirror.gcr.io"] }"""),
            new("Insecure registry", """{ "insecure-registries": ["localhost:5000"] }"""),
            new("Address pool", """{ "default-address-pools": [{ "base": "10.20.0.0/16", "size": 24 }] }"""),
            new("Log rotation", """{ "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "3" } }"""),
            new("containerd image store", """{ "features": { "containerd-snapshotter": true } }"""),
            new("Build cache cleanup", """{ "builder": { "gc": { "enabled": true, "defaultKeepStorage": "20GB" } } }"""),
            new("Debug logging", """{ "debug": true, "log-level": "debug" }"""),
            new("More parallel downloads", """{ "max-concurrent-downloads": 6 }"""),
        ];

    // Null when the text is not JSON to merge into.
    public static string? Add(string json, Snippet snippet)
    {
        JsonObject config;
        try { config = json.Trim().Length == 0 ? [] : JsonNode.Parse(json) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return null; }
        Merge(config, JsonNode.Parse(snippet.Json)!.AsObject());
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    static void Merge(JsonObject into, JsonObject add)
    {
        foreach (var (name, value) in add)
        {
            switch (into[name], value)
            {
                case (JsonArray list, JsonArray more):
                    foreach (var item in more.Where(m => !list.Any(l => JsonNode.DeepEquals(l, m)))) list.Add(item!.DeepClone());
                    break;
                case (JsonObject inner, JsonObject moreInner):
                    Merge(inner, moreInner);
                    break;
                default:
                    into[name] = value!.DeepClone();
                    break;
            }
        }
    }
}
