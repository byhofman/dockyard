using Avalonia.Controls;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Resources;

public partial class NetworksView : UserControl
{
    public NetworksView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Panel, "networks.detail", 380, minimum: 300);
        layout.Apply(false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is NetworksViewModel vm) layout.Watch(vm, () => vm.HasSelection, nameof(NetworksViewModel.HasSelection));
        };
    }
}
