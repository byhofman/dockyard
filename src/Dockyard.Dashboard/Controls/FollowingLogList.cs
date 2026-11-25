using Avalonia;
using Avalonia.Controls;
using Dockyard.Dashboard.Features.Containers;

namespace Dockyard.Dashboard.Controls;

// A log's rows that keep the newest in view while its log follows, for logs shown in a template.
sealed class FollowingLogList : ListBox
{
    public static readonly StyledProperty<LogsViewModel?> LogsProperty = AvaloniaProperty.Register<FollowingLogList, LogsViewModel?>(nameof(Logs));

    protected override Type StyleKeyOverride => typeof(ListBox);

    public LogsViewModel? Logs { get => GetValue(LogsProperty); set => SetValue(LogsProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != LogsProperty) return;
        if (change.OldValue is LogsViewModel old) old.ScrollToEndRequested -= ScrollToEnd;
        if (change.NewValue is LogsViewModel logs)
        {
            ItemsSource = logs.Rows;
            logs.ScrollToEndRequested += ScrollToEnd;
        }
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
    }

    void ScrollToEnd()
    {
        if (Logs is { Rows.Count: > 0 } logs) ScrollIntoView(logs.Rows.Count - 1);
    }
}
