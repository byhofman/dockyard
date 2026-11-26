using System.Globalization;
using Avalonia.Data.Converters;
using Dockyard.Dashboard.Store;
using Dockyard.Kind;

namespace Dockyard.Dashboard.Features.Clusters;

// A node of a cluster with the container it runs in, whose CPU and memory update as Containers samples them.
sealed record NodeRow(KindNode Node, ContainerItem? Container)
{
    public string Name => Node.Name;
    public string Role => Node.Role;
    public string State => Node.State;
}

// A cluster row's nodes, each with its container; set up by the Clusters section, which knows the engine's containers.
sealed class ClusterNodes : IValueConverter
{
    public static Func<string, ContainerItem?> Container { get; set; } = _ => null;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is KindCluster cluster ? cluster.Nodes.Select(n => new NodeRow(n, Container(n.Name))).ToList() : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
