using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed class MemoryStats
{
    [JsonPropertyName("usage")] public ulong Usage { get; set; }
    [JsonPropertyName("limit")] public ulong Limit { get; set; }
    [JsonPropertyName("stats")] public Dictionary<string, ulong>? Stats { get; set; }
    [JsonPropertyName("privateworkingset")] public ulong PrivateWorkingSet { get; set; }
}
