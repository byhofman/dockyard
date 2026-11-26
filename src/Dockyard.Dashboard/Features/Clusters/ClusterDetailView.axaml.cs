using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Dockyard.Dashboard.Features.Clusters;

// A cluster's details and actions, as Containers shows them for a picked cluster row.
public partial class ClusterDetailView : UserControl
{
    public ClusterDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ClusterDetail detail) detail.Clusters.CopyText = CopyToClipboard;
        };
    }

    async Task CopyToClipboard(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            throw new InvalidOperationException("The clipboard is not reachable from this window.");
        await clipboard.SetTextAsync(text);
    }
}
