using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<ImageSummary>))]
[JsonSerializable(typeof(PullMessage))]
partial class ImageJson : JsonSerializerContext;
