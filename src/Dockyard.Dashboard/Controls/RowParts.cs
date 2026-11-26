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
        Classes.Add("filter");
        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { all, used, unused } };
        this.Token(BackgroundProperty, "Bg").Token(BorderBrushProperty, "Line");
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
// A filter that is on or off, drawn like one segment of UsageFilterBar.
sealed class FilterSwitch : Border
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<FilterSwitch, bool>(nameof(IsChecked), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<FilterSwitch, string?>(nameof(Text));
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<FilterSwitch, Geometry?>(nameof(Icon));

    public bool IsChecked { get => GetValue(IsCheckedProperty); set => SetValue(IsCheckedProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    readonly Button segment = new();
    readonly PathIcon icon = new();
    readonly TextBlock text = new();

    public FilterSwitch()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(3);
        VerticalAlignment = VerticalAlignment.Center;
        segment.Classes.Add("segment");
        var label = new StackPanel { Children = { icon, text } };
        label.Classes.Add("label");
        segment.Content = label;
        segment.Click += (_, _) => IsChecked = !IsChecked;
        Child = segment;
        Classes.Add("filter");
        this.Token(BackgroundProperty, "Bg").Token(BorderBrushProperty, "Line");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty) segment.Classes.Set("selected", IsChecked);
        else if (change.Property == TextProperty) { text.Text = Text; Avalonia.Automation.AutomationProperties.SetName(segment, Text); }
        else if (change.Property == IconProperty) { icon.Data = Icon; icon.IsVisible = Icon is not null; }
        else if (change.Property == ToolTip.TipProperty) ToolTip.SetTip(segment, ToolTip.GetTip(this));
    }
}

// A tool that is missing, and the way to Tools: the same warning wherever it shows, above every section or in one.
sealed class ToolNotice : Border
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<ToolNotice, string?>(nameof(Text));
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CommandProperty = AvaloniaProperty.Register<ToolNotice, System.Windows.Input.ICommand?>(nameof(Command));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public System.Windows.Input.ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    protected override Type StyleKeyOverride => typeof(Border);

    readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly Button link = new() { Content = "Open Tools", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public ToolNotice()
    {
        Classes.Add("notice");
        link.Classes.Add("link");
        text.Token(TextBlock.ForegroundProperty, "Warn");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = new PathIcon { Width = 14, Height = 14, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }.Token(PathIcon.ForegroundProperty, "Warn");
        AttachedToVisualTree += (_, _) => { if (this.FindResource("IconTools") is Geometry g) icon.Data = g; };
        row.Children.Add(icon);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        Grid.SetColumn(link, 2);
        row.Children.Add(link);
        Child = row;
        this.Token(BackgroundProperty, "WarnBg");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) text.Text = Text;
        else if (change.Property == CommandProperty)
        {
            link.Command = Command;
            link.IsVisible = Command is not null;
        }
    }
}
