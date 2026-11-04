using Dockyard.Engine.Internal;

namespace Dockyard.Engine;

public static class StatsCalculator
{
    public static ContainerUsage Calculate(StatsSample sample, bool windows) => windows ? Windows(sample) : Linux(sample);

    static ContainerUsage Linux(StatsSample s)
    {
        double cpu = 0;
        if (s.PreCpuStats.SystemCpuUsage > 0)
        {
            var cpuDelta = (double)s.CpuStats.CpuUsage.TotalUsage - s.PreCpuStats.CpuUsage.TotalUsage;
            var systemDelta = (double)s.CpuStats.SystemCpuUsage - s.PreCpuStats.SystemCpuUsage;
            var cpus = s.CpuStats.OnlineCpus > 0 ? s.CpuStats.OnlineCpus : (uint)(s.CpuStats.CpuUsage.PercpuUsage?.Count ?? 1);
            if (cpuDelta > 0 && systemDelta > 0) cpu = cpuDelta / systemDelta * cpus * 100;
        }

        var usage = s.MemoryStats.Usage;
        ulong cache = 0;
        if (s.MemoryStats.Stats is { } stats)
        {
            if (stats.TryGetValue("total_inactive_file", out var v1) && v1 < usage) cache = v1;
            else if (stats.TryGetValue("inactive_file", out var v2) && v2 < usage) cache = v2;
        }
        return new ContainerUsage(cpu, usage - cache, s.MemoryStats.Limit);
    }

    static ContainerUsage Windows(StatsSample s)
    {
        double cpu = 0;
        if (RfcTime.Parse(s.Read) is { } read && RfcTime.Parse(s.PreRead) is { } preRead && preRead.Year > 1 && s.NumProcs > 0)
        {
            var possible = (double)(read - preRead).Ticks * s.NumProcs;
            var used = (double)s.CpuStats.CpuUsage.TotalUsage - s.PreCpuStats.CpuUsage.TotalUsage;
            if (possible > 0 && used > 0) cpu = used / possible * 100;
        }
        return new ContainerUsage(cpu, s.MemoryStats.PrivateWorkingSet, 0);
    }
}
