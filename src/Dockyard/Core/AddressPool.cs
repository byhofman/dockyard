using System.Text.Json.Serialization;

namespace Dockyard.Core;

sealed class AddressPool
{
    [JsonPropertyName("base")] public string Base { get; set; } = "";
    [JsonPropertyName("size")] public int Size { get; set; }
}
