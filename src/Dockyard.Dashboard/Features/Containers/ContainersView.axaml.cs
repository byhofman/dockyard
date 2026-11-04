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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var isNarrow = e.NewSize.Width < NarrowWidth;
        if (isNarrow == narrow) return;
        narrow = isNarrow;
        Root.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(440);
        Grid.SetColumn(DetailHost, 0);
        Grid.SetColumnSpan(DetailHost, narrow ? 2 : 1);
        if (!narrow) Grid.SetColumn(DetailHost, 1);
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
