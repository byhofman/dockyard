namespace Dockyard.Engine;

public readonly record struct ContainerUsage(double CpuPercent, ulong MemoryBytes, ulong MemoryLimitBytes);
