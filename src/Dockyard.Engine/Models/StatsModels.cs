using System.Text.Json.Serialization;

namespace Dockyard.Engine;

public sealed class StatsSample
{
    [JsonPropertyName("read")] public string Read { get; set; } = "";
    [JsonPropertyName("preread")] public string PreRead { get; set; } = "";
    [JsonPropertyName("num_procs")] public uint NumProcs { get; set; }
    [JsonPropertyName("cpu_stats")] public CpuStats CpuStats { get; set; } = new();
    [JsonPropertyName("precpu_stats")] public CpuStats PreCpuStats { get; set; } = new();
    [JsonPropertyName("memory_stats")] public MemoryStats MemoryStats { get; set; } = new();
}

public sealed class CpuStats
{
    [JsonPropertyName("cpu_usage")] public CpuUsage CpuUsage { get; set; } = new();
    [JsonPropertyName("system_cpu_usage")] public ulong SystemCpuUsage { get; set; }
    [JsonPropertyName("online_cpus")] public uint OnlineCpus { get; set; }
}

public sealed class CpuUsage
{
    [JsonPropertyName("total_usage")] public ulong TotalUsage { get; set; }
    [JsonPropertyName("percpu_usage")] public List<ulong>? PercpuUsage { get; set; }
}

public sealed class MemoryStats
{
    [JsonPropertyName("usage")] public ulong Usage { get; set; }
    [JsonPropertyName("limit")] public ulong Limit { get; set; }
    [JsonPropertyName("stats")] public Dictionary<string, ulong>? Stats { get; set; }
    [JsonPropertyName("privateworkingset")] public ulong PrivateWorkingSet { get; set; }
}

public readonly record struct ContainerUsage(double CpuPercent, ulong MemoryBytes, ulong MemoryLimitBytes);
