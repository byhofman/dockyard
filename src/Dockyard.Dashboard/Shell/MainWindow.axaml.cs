using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
            if (DataContext is ShellViewModel shell)
            {
                shell.Activity.Lines.CollectionChanged += (_, _) => ScrollActivityToEnd();
                shell.Activity.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActivityLog.IsOpen) && shell.Activity.IsOpen) ScrollActivityToEnd(); };
            }
        };
    }

    // The activity list is in a tab's template, in one place or side by side, so it is found rather than named.
    void ScrollActivityToEnd() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var list in this.GetVisualDescendants().OfType<ListBox>().Where(l => l.Classes.Contains("activitylist")))
            if (list.ItemCount > 0) list.ScrollIntoView(list.ItemCount - 1);
    }, DispatcherPriority.Background);

    async void CopyActivity(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(shell.Logs.Selected.Text);
    }

    // The first field, or else the button that confirms, so Enter and Escape work without the mouse.
    void FocusDialog()
    {
        var controls = DialogLayer.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToList();
        Control? target = controls.OfType<TextBox>().FirstOrDefault();
        target ??= controls.OfType<Button>().LastOrDefault(b => b.Classes.Contains("primary") || b.Classes.Contains("dangerfill"));
        target?.Focus(NavigationMethod.Tab);
    }

    // The scrim stops the mouse, but Tab would walk on into the window behind the dialog.
    void CycleDialogFocus(bool back)
    {
        var stops = DialogLayer.GetVisualDescendants().OfType<InputElement>()
            .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && KeyboardNavigation.GetIsTabStop(c))
            .ToList();
        stops = stops.Where(c => !stops.Any(other => other != c && other.IsVisualAncestorOf(c))).ToList();
        if (stops.Count == 0) return;
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var at = stops.FindIndex(c => c == focused || (focused is not null && c.IsVisualAncestorOf(focused)));
        var next = at < 0 ? (back ? stops.Count - 1 : 0) : (at + (back ? stops.Count - 1 : 1)) % stops.Count;
        stops[next].Focus(NavigationMethod.Tab);
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
            else if (e.Key == Key.Tab)
            {
                CycleDialogFocus(back: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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
        else if (SearchBox.IsFocused && DataContext is ShellViewModel shell && shell.IsSearchOpen && e.Key is Key.Down or Key.Up or Key.Enter or Key.Escape)
        {
            if (e.Key == Key.Escape) shell.IsSearchOpen = false;
            else if (e.Key == Key.Enter) shell.OpenHit();
            else shell.MoveHit(e.Key == Key.Down ? 1 : -1);
            SearchResults.ScrollIntoView(shell.HitIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsFocused)
        {
            SearchBox.Text = "";
            Focus();
            e.Handled = true;
        }
    }

    void OpenSearchResult(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchHit hit && DataContext is ShellViewModel vm)
            vm.OpenHit(hit);
    }

    void DismissToast(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: Toast toast } && DataContext is ShellViewModel vm) vm.DismissToast(toast);
    }
}
