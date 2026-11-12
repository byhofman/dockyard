namespace Dockyard.Agent;

readonly record struct MemoryInfo(long TotalKb, long AvailableKb)
{
    public static MemoryInfo Parse(string meminfo)
    {
        long Value(string key) => meminfo.Split('\n')
            .Where(l => l.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(l => long.Parse(l[(key.Length + 1)..].Trim().Split(' ')[0]))
            .FirstOrDefault();
        return new MemoryInfo(Value("MemTotal"), Value("MemAvailable"));
    }
}
