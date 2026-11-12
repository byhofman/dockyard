using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(ContextMeta))]
partial class ContextJson : JsonSerializerContext;
