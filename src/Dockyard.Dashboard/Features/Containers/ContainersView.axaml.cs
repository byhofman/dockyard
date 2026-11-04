using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Dockyard.Dashboard.Store;

namespace Dockyard.Dashboard.Features.Containers;

public partial class ContainersView : UserControl
{
    const double NarrowWidth = 820;
    bool narrow;

    public ContainersView()
    {
        InitializeComponent();
        List.ContainerPrepared += (_, e) =>
        {
            var row = List.Items[e.Index];
            e.Container.Classes.Set("header", row is GroupHeader);
            e.Container.Focusable = row is not GroupHeader;
            if (row is ContainerItem item) ViewModel?.SetVisible(item.Id, true);
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
        var sideBySide = !narrow && ViewModel?.HasDetail == true;
        Root.ColumnDefinitions[1].Width = sideBySide ? new GridLength(440) : new GridLength(0);
        Grid.SetColumn(DetailHost, sideBySide ? 1 : 0);
        Grid.SetColumnSpan(DetailHost, sideBySide ? 1 : 2);
    }

    void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { Selected: { } item } vm || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.S: vm.ToggleCommand.Execute(item); break;
            case Key.R: vm.RestartCommand.Execute(item); break;
            case Key.E: vm.ExecCommand.Execute(item); break;
            case Key.L or Key.Enter when vm.Detail is { } d: d.SelectedTab = 0; break;
            case Key.Delete when vm.Detail is { } d: d.ConfirmRemove = true; break;
            default: return;
        }
        e.Handled = true;
    }
}
