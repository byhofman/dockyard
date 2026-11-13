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
