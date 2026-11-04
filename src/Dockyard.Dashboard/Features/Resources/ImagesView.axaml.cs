using Avalonia.Controls;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Resources;

public partial class ImagesView : UserControl
{
    public ImagesView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Panel, "images.detail", 380, minimum: 300);
        layout.Apply(false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImagesViewModel vm) layout.Watch(vm, () => vm.HasSelection, nameof(ImagesViewModel.HasSelection));
        };
    }
}
