using System.Text.Json.Serialization;

namespace Dockyard.Agent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlMessage))]
partial class ControlJson : JsonSerializerContext;
