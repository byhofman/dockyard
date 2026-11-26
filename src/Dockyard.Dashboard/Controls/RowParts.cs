using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace Dockyard.Dashboard.Controls;

// The parts every list row in the dashboard is made of, so Containers, Clusters and the rest look and behave the same.

// The chevron that folds a row open and shut: compose projects, clusters.
sealed class ExpandToggle : Button
{
    public static readonly StyledProperty<bool> IsExpandedProperty = AvaloniaProperty.Register<ExpandToggle, bool>(nameof(IsExpanded));

    public bool IsExpanded { get => GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    readonly PathIcon down = new();
    readonly PathIcon right = new();

    public ExpandToggle()
    {
        Classes.Add("iconbtn");
        Padding = new Thickness(2);
        Margin = new Thickness(-6, 0, 4, 0);
        VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(this, "Expand or collapse");
        Content = new Panel { Children = { down, right } };
        AttachedToVisualTree += (_, _) =>
        {
            down.Data = this.FindResource("IconChevronDown") as Geometry;
            right.Data = this.FindResource("IconChevronRight") as Geometry;
            Show();
        };
    }

    void Show()
    {
        down.IsVisible = IsExpanded;
        right.IsVisible = !IsExpanded;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty) Show();
    }
}

// The light in front of a row: green while the thing runs or is ready.
sealed class StatusDot : Ellipse
{
    public static readonly StyledProperty<bool> IsOnProperty = AvaloniaProperty.Register<StatusDot, bool>(nameof(IsOn));

    public bool IsOn { get => GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    protected override Type StyleKeyOverride => typeof(Ellipse);

    public StatusDot()
    {
        Classes.Add("dot");
        VerticalAlignment = VerticalAlignment.Center;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOnProperty) Classes.Set("on", IsOn);
    }
}

// A row's name and the line under it.
sealed class RowLabel : StackPanel
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<RowLabel, string?>(nameof(Title));
    public static readonly StyledProperty<string?> DetailProperty = AvaloniaProperty.Register<RowLabel, string?>(nameof(Detail));
    public static readonly StyledProperty<bool> IsDetailMonoProperty = AvaloniaProperty.Register<RowLabel, bool>(nameof(IsDetailMono));

    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    public bool IsDetailMono { get => GetValue(IsDetailMonoProperty); set => SetValue(IsDetailMonoProperty, value); }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    readonly TextBlock title = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock detail = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    public RowLabel()
    {
        VerticalAlignment = VerticalAlignment.Center;
        detail.Classes.Add("muted");
        detail.Classes.Add("small");
        Children.Add(title);
        Children.Add(detail);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) title.Text = Title;
        else if (change.Property == DetailProperty)
        {
            detail.Text = Detail;
            detail.IsVisible = !string.IsNullOrEmpty(Detail);
        }
        else if (change.Property == IsDetailMonoProperty) detail.Classes.Set("mono", IsDetailMono);
    }
}

// Start or stop: the stop icon while it runs, play while it does not.
sealed class StartStopButton : Button
{
    public static readonly StyledProperty<bool> IsRunningProperty = AvaloniaProperty.Register<StartStopButton, bool>(nameof(IsRunning));

    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    protected override Type StyleKeyOverride => typeof(Button);

    readonly PathIcon stop = new();
    readonly PathIcon play = new();

    public StartStopButton()
    {
        Classes.Add("iconbtn");
        Content = new Panel { Children = { stop, play } };
        AttachedToVisualTree += (_, _) =>
        {
            stop.Data = this.FindResource("IconStop") as Geometry;
            play.Data = this.FindResource("IconPlay") as Geometry;
            Show();
        };
    }

    void Show()
    {
        stop.IsVisible = IsRunning;
        play.IsVisible = !IsRunning;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRunningProperty) Show();
    }
}

// What a section shows with nothing in it: its icon, large and muted, and a line on what to do.
sealed class EmptyState : StackPanel
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<EmptyState, string?>(nameof(Text));

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    readonly PathIcon icon = new() { Width = 40, Height = 40 };
    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };

    public EmptyState()
    {
        Spacing = 10;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        MaxWidth = 460;
        text.Classes.Add("muted");
        Children.Add(icon);
        Children.Add(text);
        AttachedToVisualTree += (_, _) =>
        {
            if (this.FindResource("Muted") is IBrush muted) icon.Foreground = muted;
            if (Icon is { } key) icon.Data = this.FindResource(key) as Geometry;
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) text.Text = Text;
        else if (change.Property == IconProperty && Icon is { } key && this.FindResource(key) is Geometry g) icon.Data = g;
    }
}