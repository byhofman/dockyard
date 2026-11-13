using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SystemDf))]
partial class SystemJson : JsonSerializerContext;
