using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Dockyard.Dashboard.Controls;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Containers;

public partial class ContainersView : UserControl
{
    const double NarrowWidth = 820;

    static readonly FilePickerFileType ComposeFiles = new("Compose files")
    {
        Patterns = ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml", "*.yaml", "*.yml"],
    };

    bool narrow;
    PanelLayout? layout;

    public ContainersView()
    {
        InitializeComponent();
        layout = new PanelLayout(Root, Splitter, DetailHost, "containers.detail", 440, minimum: 340);
        List.ContainerPrepared += (_, e) =>
        {
            if (List.Items[e.Index] is ContainerItem item) ViewModel?.SetVisible(item.Id, true);
        };
        List.ContainerClearing += (_, e) =>
        {
            if (e.Container.DataContext is ContainerItem item) ViewModel?.SetVisible(item.Id, false);
        };
        List.KeyDown += OnListKeyDown;
    }

    ContainersViewModel? ViewModel => DataContext as ContainersViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } vm)
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ContainersViewModel.HasDetail)) UpdateLayoutMode();
            };
        UpdateLayoutMode();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        narrow = e.NewSize.Width < NarrowWidth;
        UpdateLayoutMode();
    }

    void UpdateLayoutMode()
    {
        var hasDetail = ViewModel?.HasDetail == true;
        var sideBySide = !narrow && hasDetail;
        layout?.Apply(sideBySide);
        DetailHost.IsVisible = hasDetail;
        Grid.SetColumn(DetailHost, sideBySide ? 2 : 0);
        Grid.SetColumnSpan(DetailHost, sideBySide ? 1 : 3);
    }

    async void OpenComposeFile(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open compose file",
            AllowMultiple = false,
            FileTypeFilter = [ComposeFiles],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await vm.OpenComposeFile(path);
    }

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.KeyModifiers != KeyModifiers.None) return;
        if (vm.SelectedProject is { } project)
        {
            switch (e.Key)
            {
                case Key.Right when !project.IsExpanded:
                case Key.Left when project.IsExpanded:
                case Key.Enter:
                    vm.ToggleProjectCommand.Execute(project);
                    break;
                case Key.S: vm.ProjectStartStopCommand.Execute(project); break;
                case Key.R: vm.ProjectRestartCommand.Execute(project); break;
                case Key.Delete: vm.ProjectDownCommand.Execute(project); break;
                default: return;
            }
            e.Handled = true;
            return;
        }

        if (vm.Selected is not { } item) return;
        switch (e.Key)
        {
            case Key.S: vm.ToggleCommand.Execute(item); break;
            case Key.R: vm.RestartCommand.Execute(item); break;
            case Key.E: vm.ExecCommand.Execute(item); break;
            case Key.L or Key.Enter when vm.Detail is ContainerDetailViewModel d: d.SelectedTab = 0; break;
            case Key.Delete: vm.AskRemoveCommand.Execute(item); break;
            default: return;
        }
        e.Handled = true;
    }
}
