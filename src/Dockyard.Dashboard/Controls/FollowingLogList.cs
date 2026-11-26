using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dockyard.Dashboard.Features.Containers;

namespace Dockyard.Dashboard.Controls;

// A log's rows that keep the newest in view while its log follows, for logs shown in a template. Either a container's
// Logs, or, with FollowsItems, any ItemsSource that grows.
sealed class FollowingLogList : ListBox
{
    public static readonly StyledProperty<LogsViewModel?> LogsProperty = AvaloniaProperty.Register<FollowingLogList, LogsViewModel?>(nameof(Logs));
    public static readonly StyledProperty<bool> FollowsItemsProperty = AvaloniaProperty.Register<FollowingLogList, bool>(nameof(FollowsItems));

    protected override Type StyleKeyOverride => typeof(ListBox);

    public LogsViewModel? Logs { get => GetValue(LogsProperty); set => SetValue(LogsProperty, value); }
    public bool FollowsItems { get => GetValue(FollowsItemsProperty); set => SetValue(FollowsItemsProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsSourceProperty)
        {
            if (change.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= ItemsGrew;
            if (change.NewValue is INotifyCollectionChanged grows) grows.CollectionChanged += ItemsGrew;
            if (FollowsItems) ScrollToEnd();
            return;
        }
        if (change.Property != LogsProperty) return;
        if (change.OldValue is LogsViewModel oldLogs) oldLogs.ScrollToEndRequested -= ScrollToEnd;
        if (change.NewValue is LogsViewModel logs)
        {
            ItemsSource = logs.Rows;
            logs.ScrollToEndRequested += ScrollToEnd;
        }
    }

    void ItemsGrew(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (FollowsItems && e.Action == NotifyCollectionChangedAction.Add) Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Logs is { } logs) logs.ScrollToEndRequested -= ScrollToEnd;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Logs is { } logs)
        {
            logs.ScrollToEndRequested -= ScrollToEnd;
            logs.ScrollToEndRequested += ScrollToEnd;
        }
        if (FollowsItems) Dispatcher.UIThread.Post(ScrollToEnd, DispatcherPriority.Background);
    }

    void ScrollToEnd()
    {
        if (ItemCount > 0 && this.IsAttachedToVisualTree()) ScrollIntoView(ItemCount - 1);
    }
}
