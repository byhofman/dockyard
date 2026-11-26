using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using System.Globalization;

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
// A container's or load balancer's published ports as links to http://127.0.0.1:<port>: the port alone, or the whole URL.
sealed class PortLinks : ItemsControl
{
    public static readonly StyledProperty<bool> ShowUrlProperty = AvaloniaProperty.Register<PortLinks, bool>(nameof(ShowUrl));
    // Opens a port; the section's own, so a failure is said where it happened.
    public static readonly StyledProperty<System.Windows.Input.ICommand?> OpenCommandProperty = AvaloniaProperty.Register<PortLinks, System.Windows.Input.ICommand?>(nameof(OpenCommand));

    public bool ShowUrl { get => GetValue(ShowUrlProperty); set => SetValue(ShowUrlProperty, value); }
    public System.Windows.Input.ICommand? OpenCommand { get => GetValue(OpenCommandProperty); set => SetValue(OpenCommandProperty, value); }

    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public PortLinks()
    {
        VerticalAlignment = VerticalAlignment.Center;
        ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { ItemSpacing = 10 });
        ItemTemplate = new FuncDataTemplate<Store.PublishedPort>((port, _) =>
        {
            var icon = new PathIcon { Width = 10, Height = 10 };
            if (this.FindResource("IconOpenExternal") is Geometry g) icon.Data = g;
            var text = new TextBlock { Text = ShowUrl ? port.Url : port.HostPort.ToString(CultureInfo.InvariantCulture) };
            if (ShowUrl) text.Classes.Add("mono");
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { text, icon } };
            label.Classes.Add("label");
            var link = new Button { Content = label, CommandParameter = port };
            link.Classes.Add("link");
            ToolTip.SetTip(link, ShowUrl ? port.Text : port.Url);
            link.Click += (_, _) => { if (OpenCommand?.CanExecute(port) == true) OpenCommand.Execute(port); };
            return link;
        });
    }
}

// The All / In use / Unused filter of a list whose items something may use, over a UsageFilter as its DataContext.
sealed class UsageFilterBar : Border
{
    readonly Button all = Segment("All", null);
    readonly Button used = Segment("Used", "Used by a container, running or not");
    readonly Button unused = Segment("Unused", "No container uses it");
    Features.Resources.UsageFilter? filter;

    static Button Segment(string kind, string? tip)
    {
        var b = new Button { CommandParameter = kind };
        b.Classes.Add("segment");
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }

    public UsageFilterBar()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(3);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { all, used, unused } };
        AttachedToVisualTree += (_, _) =>
        {
            if (this.FindResource("Bg") is IBrush bg) Background = bg;
            if (this.FindResource("Line") is IBrush line) BorderBrush = line;
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (filter is not null) filter.PropertyChanged -= Update;
        filter = DataContext as Features.Resources.UsageFilter;
        if (filter is null) return;
        filter.PropertyChanged += Update;
        foreach (var b in new[] { all, used, unused }) b.Command = filter.ShowCommand;
        Update(null, null!);
    }

    void Update(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (filter is null) return;
        all.Content = filter.AllText;
        used.Content = filter.UsedText;
        unused.Content = filter.UnusedText;
        all.Classes.Set("selected", filter.IsAll);
        used.Classes.Set("selected", filter.IsUsed);
        unused.Classes.Set("selected", filter.IsUnused);
    }
}

// A section's line on what is missing or what to do, with a link to act on it when there is one.
sealed class SectionHint : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<SectionHint, string?>(nameof(Text));
    public static readonly StyledProperty<string?> LinkTextProperty = AvaloniaProperty.Register<SectionHint, string?>(nameof(LinkText));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> LinkCommandProperty = AvaloniaProperty.Register<SectionHint, System.Windows.Input.ICommand?>(nameof(LinkCommand));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? LinkText { get => GetValue(LinkTextProperty); set => SetValue(LinkTextProperty, value); }
    public System.Windows.Input.ICommand? LinkCommand { get => GetValue(LinkCommandProperty); set => SetValue(LinkCommandProperty, value); }

    protected override Type StyleKeyOverride => typeof(StackPanel);

    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Button link = new() { IsVisible = false };

    public SectionHint()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 10;
        text.Classes.Add("muted");
        link.Classes.Add("link");
        Children.Add(text);
        Children.Add(link);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) text.Text = Text;
        else if (change.Property == LinkTextProperty || change.Property == LinkCommandProperty)
        {
            link.Content = LinkText;
            link.Command = LinkCommand;
            link.IsVisible = !string.IsNullOrEmpty(LinkText) && LinkCommand is not null;
        }
    }
}