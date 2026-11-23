using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dockyard.Dashboard.Controls;

namespace Dockyard.Dashboard.Features.Resources;

public partial class VolumesView : UserControl
{
    public VolumesView()
    {
        InitializeComponent();
        var layout = new PanelLayout(Layout, Splitter, Panel, "volumes.detail", 380, minimum: 300);
        layout.Apply(false);
        ListKeys.RemoveOnDelete(List, () => (DataContext as VolumesViewModel)?.RemoveCommand);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is VolumesViewModel vm) layout.Watch(vm, () => vm.HasSelection, nameof(VolumesViewModel.HasSelection));
        };
    }

    async void ExportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not VolumesViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Export a copy into" });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } parent) return;
        await vm.Export(parent);
    }
}
