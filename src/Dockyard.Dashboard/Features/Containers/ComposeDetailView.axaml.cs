using Avalonia.Controls;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Containers;

public partial class ComposeDetailView : UserControl
{
    public ComposeDetailView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Bottom, "compose.output", 260, minimum: 160);
        layout.Apply(false);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ComposeDetailViewModel vm)
                layout.Watch(vm, () => vm.HasBottom, nameof(ComposeDetailViewModel.HasBottom));
        };
    }
}
