using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dockyard.Engine.Transport;

namespace Dockyard.Engine;

public sealed record ResolvedContext(string Name, DockerEndpoint Endpoint);

public static class DockerContext
{
    public static string DefaultHost =>
        OperatingSystem.IsWindows() ? "npipe:////./pipe/docker_engine" : "unix:///var/run/docker.sock";

    public static string ConfigDir(Func<string, string?> env) =>
        env("DOCKER_CONFIG") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");

    public static ResolvedContext Resolve(string? configDir = null, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        configDir ??= ConfigDir(env);

        var name = env("DOCKER_CONTEXT") is { Length: > 0 } explicitName ? explicitName
            : env("DOCKER_HOST") is { Length: > 0 } ? "default"
            : ReadCurrentContext(configDir) ?? "default";

        if (name == "default")
            return new("default", DockerEndpoint.Parse(env("DOCKER_HOST") is { Length: > 0 } host ? host : DefaultHost));

        var meta = Path.Combine(configDir, "contexts", "meta", Hash(name), "meta.json");
        if (!File.Exists(meta)) throw new DockerContextException($"Docker context '{name}' does not exist");

        var doc = JsonSerializer.Deserialize(File.ReadAllText(meta), ContextJson.Default.ContextMeta);
        var endpoint = doc?.Endpoints?.GetValueOrDefault("docker")?.Host
            ?? throw new DockerContextException($"Docker context '{name}' has no docker endpoint");
        return new(name, DockerEndpoint.Parse(endpoint));
    }

    static string? ReadCurrentContext(string configDir)
    {
        var file = Path.Combine(configDir, "config.json");
        if (!File.Exists(file)) return null;
        var config = JsonSerializer.Deserialize(File.ReadAllText(file), ContextJson.Default.CliConfig);
        return config?.CurrentContext is { Length: > 0 } c ? c : null;
    }

    static string Hash(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
}

sealed class CliConfig
{
    public string? CurrentContext { get; set; }
}

sealed class ContextMeta
{
    public Dictionary<string, ContextEndpoint>? Endpoints { get; set; }
}

sealed class ContextEndpoint
{
    public string? Host { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(ContextMeta))]
partial class ContextJson : JsonSerializerContext;
