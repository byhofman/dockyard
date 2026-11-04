using System.ComponentModel;
using Avalonia.Controls;
using Dockyard.Dashboard.Features.Resources;

namespace Dockyard.Dashboard.Controls;

// Grid layout: content | splitter | panel (columns), or content / splitter / panel (rows). The panel size persists per key.
sealed class PanelLayout
{
    const double SplitterSize = 5;

    readonly Grid grid;
    readonly GridSplitter splitter;
    readonly Control panel;
    readonly string key;
    readonly double fallback;
    readonly double minimum;
    readonly bool rows;

    public PanelLayout(Grid grid, GridSplitter splitter, Control panel, string key, double fallback, double minimum = 260)
    {
        this.grid = grid;
        this.splitter = splitter;
        this.panel = panel;
        this.key = key;
        this.fallback = fallback;
        this.minimum = minimum;
        rows = grid.RowDefinitions.Count >= 3;
        splitter.DragCompleted += (_, _) => DashboardSettings.Current.SetPanelSize(key, Actual);
    }

    double Actual => rows ? grid.RowDefinitions[2].ActualHeight : grid.ColumnDefinitions[2].ActualWidth;

    public void Watch(INotifyPropertyChanged? source, Func<bool> visible, params string[] properties)
    {
        if (source is null) return;
        source.PropertyChanged += (_, e) =>
        {
            if (properties.Contains(e.PropertyName)) Apply(visible());
        };
        Apply(visible());
    }

    public void Apply(bool visible)
    {
        var size = visible ? DashboardSettings.Current.PanelSize(key, fallback) : 0;
        splitter.IsVisible = visible;
        panel.IsVisible = visible;
        if (rows)
        {
            grid.RowDefinitions[1].Height = new GridLength(visible ? SplitterSize : 0);
            grid.RowDefinitions[2].MinHeight = visible ? minimum / 2 : 0;
            grid.RowDefinitions[2].Height = new GridLength(size);
        }
        else
        {
            grid.ColumnDefinitions[1].Width = new GridLength(visible ? SplitterSize : 0);
            grid.ColumnDefinitions[2].MinWidth = visible ? minimum : 0;
            grid.ColumnDefinitions[2].Width = new GridLength(size);
        }
    }
}
