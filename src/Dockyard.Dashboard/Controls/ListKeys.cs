using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;

namespace Dockyard.Dashboard.Controls;

static class ListKeys
{
    // Delete or Backspace on the list asks to remove what is picked, as the row's own button does.
    public static void RemoveOnDelete(ListBox list, Func<ICommand?> command) => list.KeyDown += (_, e) =>
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Delete or Key.Back) || command() is not { } c || !c.CanExecute(null)) return;
        c.Execute(null);
        e.Handled = true;
    };
}
