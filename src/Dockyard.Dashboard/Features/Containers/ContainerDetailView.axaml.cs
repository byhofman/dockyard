using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Dockyard.Dashboard.Features.Containers;

public partial class ContainerDetailView : UserControl
{
    LogsViewModel? logs;

    public ContainerDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (logs is not null) logs.ScrollToEndRequested -= ScrollToEnd;
            logs = (DataContext as ContainerDetailViewModel)?.Logs;
            if (logs is not null) logs.ScrollToEndRequested += ScrollToEnd;
        };
    }

    void ScrollToEnd()
    {
        if (logs is { Rows.Count: > 0 } l) LogList.ScrollIntoView(l.Rows.Count - 1);
    }

    async void CopySection(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InspectSection section } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(section.CopyText);
    }
}
