using System.Globalization;
using Avalonia.Data.Converters;
using Dockyard.Dashboard.Store;
using Dockyard.Kind;

namespace Dockyard.Dashboard.Features.Clusters;

// A node of a cluster with the container it runs in, whose CPU and memory update as Containers samples them, and whether
// Kubernetes counts it as Ready: its kubelet talks to the API server.
sealed class NodeRow(KindNode node, ContainerItem? container) : Shell.Observable
{
    bool? ready;

    public KindNode Node { get; } = node;
    public ContainerItem? Container { get; } = container;
    public string Name => Node.Name;
    public string Role => Node.Role;
    public bool? Ready { get => ready; set { if (Set(ref ready, value)) Raise(nameof(IsReady), nameof(StateText)); } }
    public bool IsReady => ready == true;
    public string StateText => !Node.IsRunning ? Node.State : ready switch { true => "Ready", false => "Not ready", null => "Checking…" };
}

// A cluster row's nodes while it is unfolded, and whether it is; set up by the Kubernetes section.
sealed class ClusterNodes : IValueConverter
{
    public static Func<KindCluster, IReadOnlyList<NodeRow>?> Rows { get; set; } = _ => null;
    public static Func<KindCluster, bool> Unfolded { get; set; } = _ => false;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not KindCluster cluster ? null
        : parameter as string == "unfolded" ? Unfolded(cluster)
        : parameter as string == "folded" ? !Unfolded(cluster)
        : Unfolded(cluster) ? Rows(cluster) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
