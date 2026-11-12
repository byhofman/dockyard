using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(DockerEvent))]
partial class EventJson : JsonSerializerContext;
