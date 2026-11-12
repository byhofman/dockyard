using System.Text.Json.Serialization;

namespace Dockyard.Engine;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<NetworkSummary>))]
[JsonSerializable(typeof(NetworkSummary))]
[JsonSerializable(typeof(NetworkCreateRequest))]
[JsonSerializable(typeof(NetworkCreateResponse))]
[JsonSerializable(typeof(NetworkConnectRequest))]
[JsonSerializable(typeof(NetworkDisconnectRequest))]
partial class NetworkJson : JsonSerializerContext;
