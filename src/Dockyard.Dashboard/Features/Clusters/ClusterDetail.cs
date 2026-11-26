using System.ComponentModel;

namespace Dockyard.Dashboard.Features.Clusters;

// A cluster's details as Containers shows them when its row is picked: the Kubernetes section's own, pointed at it. Its
// close button clears that section's pick, which closes this too.
sealed class ClusterDetail : IDisposable
{
    readonly Action close;

    public ClusterDetail(ClustersViewModel clusters, Action close)
    {
        Clusters = clusters;
        this.close = close;
        clusters.PropertyChanged += Closed;
    }

    public ClustersViewModel Clusters { get; }

    void Closed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClustersViewModel.Selected) && Clusters.Selected is null) close();
    }

    public void Dispose() => Clusters.PropertyChanged -= Closed;
}
