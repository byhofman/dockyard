using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace Dockyard.Tray;

// The system's menu as a copy of TrayMenu. Items are changed in place and only added or taken out when the set that shows
// changes: a native menu that is replaced while it is open closes under the pointer.
sealed class NativeTrayMenu
{
    readonly ConditionalWeakTable<TrayMenuItem, NativeMenuItemBase> copies = [];

    public NativeMenu Menu { get; } = new();

    public void Sync(TrayMenu model) => Fill(Menu, model.Visible);

    void Fill(NativeMenu menu, IReadOnlyList<TrayMenuItem> items)
    {
        var wanted = items.Select(Copy).ToList();
        if (!menu.Items.SequenceEqual(wanted))
        {
            menu.Items.Clear();
            foreach (var item in wanted) menu.Items.Add(item);
        }
        foreach (var item in items)
        {
            if (Copy(item) is not NativeMenuItem native) continue;
            native.Header = item.Header;
            native.IsEnabled = item.IsEnabled;
            if (item.IsChecked is { } isChecked) native.IsChecked = isChecked;
            if (item.Items.Count > 0) Fill(native.Menu ??= new NativeMenu(), item.Items);
        }
    }

    NativeMenuItemBase Copy(TrayMenuItem item)
    {
        if (copies.TryGetValue(item, out var existing)) return existing;
        NativeMenuItemBase copy;
        if (item.IsSeparator) copy = new NativeMenuItemSeparator();
        else
        {
            var native = new NativeMenuItem(item.Header);
            if (item.IsRadio) native.ToggleType = MenuItemToggleType.Radio;
            if (item.Click is { } click) native.Click += (_, _) => click();
            copy = native;
        }
        copies.Add(item, copy);
        return copy;
    }
}
