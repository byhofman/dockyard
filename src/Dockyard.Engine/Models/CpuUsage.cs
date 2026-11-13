using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed class CpuUsage
{
    [JsonPropertyName("total_usage")] public ulong TotalUsage { get; set; }
    [JsonPropertyName("percpu_usage")] public List<ulong>? PercpuUsage { get; set; }
}
