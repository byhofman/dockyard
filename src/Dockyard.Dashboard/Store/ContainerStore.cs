using System.Collections.ObjectModel;
using Dockyard.Dashboard.Shell;
using Dockyard.Engine;

namespace Dockyard.Dashboard.Store;

sealed record PublishedPort(int HostPort, int ContainerPort, string Protocol)
{
    public string Url => $"http://127.0.0.1:{HostPort}";
    public string Text => $"{HostPort}→{ContainerPort}/{Protocol}";
}

sealed record GroupHeader(string Title, int Running, int Total)
{
    public string Summary => $"{Running}/{Total} running";
}

sealed class ContainerItem(string id) : Observable
{
    public const string ProjectLabel = "com.docker.compose.project";
    public const string ServiceLabel = "com.docker.compose.service";
    const int HistoryLength = 60;

    string name = "", image = "", state = "", status = "", project = "", service = "";
    IReadOnlyList<PublishedPort> ports = [];
    double cpu;
    ulong memory;
    bool hasUsage;
    double[] cpuHistory = [], memoryHistory = [];

    public string Id { get; } = id;
    public string ImageId { get; private set; } = "";
    public IReadOnlyDictionary<string, string> Labels { get; private set; } = new Dictionary<string, string>();
    public IReadOnlyList<string> VolumeNames { get; private set; } = [];
    public string ShortId => Id[..Math.Min(12, Id.Length)];
    public string Name { get => name; private set => Set(ref name, value); }
    public string Image { get => image; private set => Set(ref image, value); }
    public string Status { get => status; private set => Set(ref status, value); }
    public string Project { get => project; private set => Set(ref project, value); }
    public string Service { get => service; private set => Set(ref service, value); }
    public IReadOnlyList<PublishedPort> Ports { get => ports; private set => Set(ref ports, value); }
    public string PortsText => string.Join("  ", Ports.Select(p => p.Text));

    public string State
    {
        get => state;
        private set { if (Set(ref state, value)) Raise(nameof(IsRunning), nameof(IsStopped), nameof(ToggleText)); }
    }

    public bool IsRunning => State is "running" or "restarting" or "paused";
    public bool IsStopped => !IsRunning;
    public string ToggleText => IsRunning ? "Stop" : "Start";

    public bool HasUsage { get => hasUsage; private set => Set(ref hasUsage, value); }
    public double CpuPercent { get => cpu; private set => Set(ref cpu, value); }
    public ulong MemoryBytes { get => memory; private set => Set(ref memory, value); }
    public string UsageText => HasUsage ? $"{CpuPercent:0.0}%  {Format.Bytes(MemoryBytes)}" : "";
    public double[] CpuHistory { get => cpuHistory; private set => Set(ref cpuHistory, value); }
    public double[] MemoryHistory { get => memoryHistory; private set => Set(ref memoryHistory, value); }

    public void Update(ContainerSummary s)
    {
        Name = s.Name;
        ImageId = s.ImageID;
        Labels = s.Labels ?? [];
        VolumeNames = (s.Mounts ?? []).Where(m => m.Type == "volume" && m.Name is not null).Select(m => m.Name!).ToList();
        Image = s.Image;
        State = s.State;
        Status = s.Status;
        Project = s.Labels?.GetValueOrDefault(ProjectLabel) ?? "";
        Service = s.Labels?.GetValueOrDefault(ServiceLabel) ?? "";
        var newPorts = PortsOf(s);
        if (!newPorts.SequenceEqual(Ports))
        {
            Ports = newPorts;
            Raise(nameof(PortsText));
        }
        if (!IsRunning) ClearUsage();
    }

    public void ApplyUsage(ContainerUsage usage)
    {
        CpuPercent = usage.CpuPercent;
        MemoryBytes = usage.MemoryBytes;
        HasUsage = true;
        CpuHistory = Append(CpuHistory, usage.CpuPercent);
        MemoryHistory = Append(MemoryHistory, usage.MemoryBytes);
        Raise(nameof(UsageText));
    }

    public void ClearUsage()
    {
        if (!HasUsage) return;
        HasUsage = false;
        CpuHistory = [];
        MemoryHistory = [];
        Raise(nameof(UsageText));
    }

    static double[] Append(double[] history, double value)
    {
        var keep = Math.Min(history.Length, HistoryLength - 1);
        var next = new double[keep + 1];
        Array.Copy(history, history.Length - keep, next, 0, keep);
        next[keep] = value;
        return next;
    }

    internal static List<PublishedPort> PortsOf(ContainerSummary s) => s.Ports
        .Where(p => p.PublicPort is > 0)
        .Select(p => new PublishedPort(p.PublicPort!.Value, p.PrivatePort, p.Type))
        .Distinct()
        .OrderBy(p => p.HostPort)
        .ToList();
}

sealed class ContainerStore
{
    readonly Dictionary<string, ContainerItem> items = new();
    string filter = "";

    public ObservableCollection<object> Rows { get; } = [];
    public IReadOnlyCollection<ContainerItem> Items => items.Values;
    public event Action? RowsChanging;
    public event Action? RowsChanged;

    public string Filter
    {
        get => filter;
        set
        {
            if (filter == (value ?? "")) return;
            filter = value ?? "";
            Rebuild();
        }
    }

    public ContainerItem? Get(string id) => items.GetValueOrDefault(id);

    public void Reset(IEnumerable<ContainerSummary> containers)
    {
        var seen = new HashSet<string>();
        foreach (var s in containers)
        {
            seen.Add(s.Id);
            if (!items.TryGetValue(s.Id, out var item)) items[s.Id] = item = new ContainerItem(s.Id);
            item.Update(s);
        }
        foreach (var gone in items.Keys.Where(k => !seen.Contains(k)).ToList()) items.Remove(gone);
        Rebuild();
    }

    public void Upsert(ContainerSummary s)
    {
        var isNew = !items.TryGetValue(s.Id, out var item);
        if (isNew) items[s.Id] = item = new ContainerItem(s.Id);
        var (name, project, running) = (item!.Name, item.Project, item.IsRunning);
        item.Update(s);
        if (isNew || name != item.Name || project != item.Project || running != item.IsRunning) Rebuild();
    }

    public void Remove(string id)
    {
        if (items.Remove(id)) Rebuild();
    }

    public void Clear()
    {
        items.Clear();
        Rebuild();
    }

    void Rebuild()
    {
        var rows = BuildRows(items.Values, filter);
        if (rows.SequenceEqual(Rows)) return;
        RowsChanging?.Invoke();
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        RowsChanged?.Invoke();
    }

    internal static List<object> BuildRows(IEnumerable<ContainerItem> all, string filter)
    {
        var matching = all.Where(i => Matches(i, filter)).ToList();
        var groups = matching.Where(i => i.Project.Length > 0)
            .GroupBy(i => i.Project, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var standalone = matching.Where(i => i.Project.Length == 0).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var rows = new List<object>();
        foreach (var g in groups)
        {
            var members = g.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
            rows.Add(new GroupHeader(g.Key, members.Count(m => m.IsRunning), members.Count));
            rows.AddRange(members);
        }
        if (groups.Count > 0 && standalone.Count > 0)
            rows.Add(new GroupHeader("Standalone", standalone.Count(m => m.IsRunning), standalone.Count));
        rows.AddRange(standalone);
        return rows;
    }

    static bool Matches(ContainerItem i, string filter) =>
        filter.Length == 0
        || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || i.Image.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || i.Project.Contains(filter, StringComparison.OrdinalIgnoreCase);
}
