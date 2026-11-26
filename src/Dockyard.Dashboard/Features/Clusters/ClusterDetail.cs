using System.ComponentModel;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Shell;
using Dockyard.Engine;
using Dockyard.Kind;

namespace Dockyard.Dashboard.Features.Clusters;

// A cluster's details as Containers shows them when its row is picked, pointed at it, with its nodes' logs combined. Its
// close button clears the clusters' pick, which closes this too.
sealed class ClusterDetail : Observable, IDisposable
{
    public const int LogsTab = 4;

    readonly Action close;
    readonly ContainersViewModel owner;
    readonly KindCluster cluster;
    int selectedTab;

    public ClusterDetail(ClustersViewModel clusters, KindCluster cluster, ContainersViewModel owner, Action close)
    {
        Clusters = clusters;
        this.cluster = cluster;
        this.owner = owner;
        this.close = close;
        clusters.PropertyChanged += Closed;
        OpenLogsCommand = new Command(async () =>
        {
            if (owner.Session.Client is not { } client) return;
            var sources = await Sources(client);
            owner.OpenLogsSideBySide(sources.Select(s => (s.Id, s with { Prefix = null })).ToList());
        });
    }

    public ClustersViewModel Clusters { get; }
    public string Name => cluster.Name;
    public LogsViewModel Logs { get; } = new();
    public Command OpenLogsCommand { get; }

    public int SelectedTab
    {
        get => selectedTab;
        set
        {
            if (!Set(ref selectedTab, value)) return;
            if (value == LogsTab) _ = StartLogs();
            else Logs.Dispose();
        }
    }

    async Task StartLogs()
    {
        if (owner.Session.Client is not { } client) return;
        var sources = await Sources(client);
        if (SelectedTab == LogsTab) Logs.Start(client, sources);
    }

    // kind starts its nodes with a TTY, so their log stream is raw rather than multiplexed; read as multiplexed it
    // shows nothing. Each node says which it is.
    async Task<List<LogSource>> Sources(DockerClient client)
    {
        var sources = new List<LogSource>();
        foreach (var node in cluster.Nodes)
        {
            bool tty;
            try { tty = (await client.InspectContainerAsync(node.Name)).Config.Tty; }
            catch (Exception ex) when (ex is DockerException or EngineUnavailableException or IOException) { tty = true; }
            sources.Add(new LogSource(node.Name, tty, PrefixOf(cluster.Name, node.Name)));
        }
        return sources;
    }

    public static string PrefixOf(string cluster, string node) => node.StartsWith(cluster + "-") ? node[(cluster.Length + 1)..] : node;

    void Closed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClustersViewModel.Selected) && Clusters.Selected is null) close();
    }

    public void Dispose()
    {
        Clusters.PropertyChanged -= Closed;
        Logs.Dispose();
    }
}
