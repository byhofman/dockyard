using System.Text.Json.Serialization;
using Dockyard.Engine;

namespace Dockyard.Agent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentRequest))]
[JsonSerializable(typeof(AgentReply))]
[JsonSerializable(typeof(List<ClusterWant>))]
[JsonSerializable(typeof(Dictionary<string, int>), TypeInfoPropertyName = "DictionaryStringInt32")]
partial class AgentApiJson : JsonSerializerContext;
