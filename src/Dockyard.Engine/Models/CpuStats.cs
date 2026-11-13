using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed class CpuStats
{
    [JsonPropertyName("cpu_usage")] public CpuUsage CpuUsage { get; set; } = new();
    [JsonPropertyName("system_cpu_usage")] public ulong SystemCpuUsage { get; set; }
    [JsonPropertyName("online_cpus")] public uint OnlineCpus { get; set; }
}
