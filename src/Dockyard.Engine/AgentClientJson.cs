using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentMessage))]
partial class AgentClientJson : JsonSerializerContext;
