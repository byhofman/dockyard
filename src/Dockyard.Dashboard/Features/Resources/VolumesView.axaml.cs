using Avalonia.Controls;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Resources;

public partial class VolumesView : UserControl
{
    public VolumesView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Panel, "volumes.detail", 380, minimum: 300);
        layout.Apply(false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is VolumesViewModel vm) layout.Watch(vm, () => vm.HasSelection, nameof(VolumesViewModel.HasSelection));
        };
    }
}
