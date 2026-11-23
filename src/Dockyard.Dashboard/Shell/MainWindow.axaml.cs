using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Dockyard.Dashboard.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShellViewModel vm)
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ShellViewModel.HasDialog) && vm.HasDialog) Dispatcher.UIThread.Post(FocusDialog, DispatcherPriority.Loaded);
                };
        };
    }

    // The first field, or else the button that confirms, so Enter and Escape work without the mouse.
    void FocusDialog()
    {
        var controls = DialogLayer.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToList();
        Control? target = controls.OfType<TextBox>().FirstOrDefault();
        target ??= controls.OfType<Button>().LastOrDefault(b => b.Classes.Contains("primary") || b.Classes.Contains("dangerfill"));
        target?.Focus(NavigationMethod.Tab);
    }

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is ShellViewModel { Dialog: { } dialog })
        {
            if (e.Key == Key.Escape)
            {
                dialog.CancelCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }
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
