using Avalonia.Controls;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Builds;

public partial class BuildsView : UserControl
{
    public BuildsView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Panel, "builds.detail", 460, minimum: 320);
        layout.Apply(false);
        ListKeys.RemoveOnDelete(List, () => (DataContext as BuildsViewModel)?.RemoveCommand);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is BuildsViewModel vm) layout.Watch(vm, () => vm.HasSelection, nameof(BuildsViewModel.HasSelection));
        };
    }
}
