using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Dockyard.Dashboard.Features.Files;

public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not FileBrowserViewModel vm) return;
            vm.EnsureLoaded();
            // The deepest folder is the one that matters when the path is wider than the pane.
            vm.Crumbs.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(() => CrumbScroll.ScrollToEnd(), DispatcherPriority.Loaded);
        };
        List.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Back || DataContext is not FileBrowserViewModel vm || !vm.UpCommand.CanExecute(null)) return;
            vm.UpCommand.Execute(null);
            e.Handled = true;
        };
    }

    async void DownloadClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel { SelectedFile: { } row } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = row.Name });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await vm.Download(stream);
    }

    async void UploadClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not FileBrowserViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        if (files.Count == 0) return;
        await using var stream = await files[0].OpenReadAsync();
        await vm.Upload(files[0].Name, stream);
    }
}
