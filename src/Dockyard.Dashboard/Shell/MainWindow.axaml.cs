using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Dockyard.Dashboard.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(ShellViewModel.ShortcutModifier))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsFocused)
        {
            SearchBox.Text = "";
            Focus();
            e.Handled = true;
        }
    }

    void DismissToast(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Toast toast } && DataContext is ShellViewModel vm) vm.DismissToast(toast);
    }
}
