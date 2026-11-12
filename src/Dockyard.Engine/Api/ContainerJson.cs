using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ContainerSummary>))]
[JsonSerializable(typeof(ContainerInspect))]
[JsonSerializable(typeof(CreateContainerRequest))]
[JsonSerializable(typeof(CreateContainerResponse))]
partial class ContainerJson : JsonSerializerContext;
