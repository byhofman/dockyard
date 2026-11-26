using System.ComponentModel;
using Dockyard.Dashboard.Features.Containers;
using Dockyard.Dashboard.Shell;
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
        OpenLogsCommand = new Command(() =>
        {
            owner.OpenLogsSideBySide(cluster.Nodes.Select(n => (n.Name, new LogSource(n.Name, false, null))).ToList());
            return Task.CompletedTask;
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
            if (value == LogsTab && owner.Session.Client is { } client) Logs.Start(client, Sources());
            else Logs.Dispose();
        }
    }

    List<LogSource> Sources() => cluster.Nodes.Select(n => new LogSource(n.Name, false, n.Name.StartsWith(cluster.Name + "-") ? n.Name[(cluster.Name.Length + 1)..] : n.Name)).ToList();

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
