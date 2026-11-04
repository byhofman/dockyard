using System.Text.Json;
using Dockyard.Engine.Tests.Support;

namespace Dockyard.Engine.Tests;

public sealed class StatsTests
{
    const string LinuxSample = """
        {"read":"2026-11-04T10:00:01.5Z","preread":"2026-11-04T10:00:00.5Z",
         "cpu_stats":{"cpu_usage":{"total_usage":200},"system_cpu_usage":2000,"online_cpus":4},
         "precpu_stats":{"cpu_usage":{"total_usage":100},"system_cpu_usage":1000,"online_cpus":4},
         "memory_stats":{"usage":1000,"limit":8000,"stats":{"inactive_file":200}}}
        """;

    const string WindowsSample = """
        {"read":"2026-11-04T10:00:01.000000000Z","preread":"2026-11-04T10:00:00.000000000Z","num_procs":2,
         "cpu_stats":{"cpu_usage":{"total_usage":15000000}},
         "precpu_stats":{"cpu_usage":{"total_usage":10000000}},
         "memory_stats":{"privateworkingset":52428800}}
        """;

    static StatsSample Parse(string json) => JsonSerializer.Deserialize(json, StatsJson.Default.StatsSample)!;

    [Fact]
    public void Linux_cpu_and_memory_match_docker_stats()
    {
        var u = StatsCalculator.Calculate(Parse(LinuxSample), windows: false);
        Assert.Equal(40.0, u.CpuPercent, 3);
        Assert.Equal(800UL, u.MemoryBytes);
        Assert.Equal(8000UL, u.MemoryLimitBytes);
    }

    [Fact]
    public void Linux_cgroup_v1_uses_total_inactive_file()
    {
        var u = StatsCalculator.Calculate(Parse(LinuxSample.Replace("\"inactive_file\"", "\"total_inactive_file\"")), windows: false);
        Assert.Equal(800UL, u.MemoryBytes);
    }

    [Fact]
    public void First_linux_sample_without_previous_reports_zero_cpu()
    {
        var u = StatsCalculator.Calculate(Parse(LinuxSample.Replace("\"system_cpu_usage\":1000", "\"system_cpu_usage\":0")), windows: false);
        Assert.Equal(0.0, u.CpuPercent);
    }

    [Fact]
    public void Windows_cpu_and_memory_match_docker_stats()
    {
        var u = StatsCalculator.Calculate(Parse(WindowsSample), windows: true);
        Assert.Equal(25.0, u.CpuPercent, 3);
        Assert.Equal(52428800UL, u.MemoryBytes);
        Assert.Equal(0UL, u.MemoryLimitBytes);
    }

    [Fact]
    public async Task StatsAsync_streams_usage()
    {
        var stub = StubHandler.WithPing().Lines(LinuxSample.ReplaceLineEndings(""), LinuxSample.ReplaceLineEndings(""));
        using var client = stub.Client();

        var samples = new List<ContainerUsage>();
        await foreach (var u in client.StatsAsync("abc", TestContext.Current.CancellationToken)) samples.Add(u);

        Assert.Equal("/v1.47/containers/abc/stats?stream=1", stub.Path(1));
        Assert.Equal(2, samples.Count);
        Assert.Equal(40.0, samples[0].CpuPercent, 3);
    }
}
