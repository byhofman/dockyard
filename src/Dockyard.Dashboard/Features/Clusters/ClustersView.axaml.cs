using Avalonia.Controls;
using Avalonia.Input.Platform;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Clusters;

public partial class ClustersView : UserControl
{
    public ClustersView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Panel, "clusters.detail", 380, minimum: 300);
        layout.Apply(false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ClustersViewModel vm) return;
            layout.Watch(vm, () => vm.HasSelection, nameof(ClustersViewModel.HasSelection));
            vm.CopyText = async text =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            };
        };
    }
}
