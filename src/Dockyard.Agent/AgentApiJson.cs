using System.Text.Json.Serialization;

namespace Dockyard.Agent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentReply))]
[JsonSerializable(typeof(Dictionary<string, int>), TypeInfoPropertyName = "DictionaryStringInt32")]
partial class AgentApiJson : JsonSerializerContext;
