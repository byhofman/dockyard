using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Controls;

// A run of the setup facade: its current step "n of m" over a bar, how it failed, and every line it reported, newest in view.
sealed class ProgressPanel : DockPanel
{
    public static readonly StyledProperty<string?> HeadingProperty = AvaloniaProperty.Register<ProgressPanel, string?>(nameof(Heading));
    public static readonly StyledProperty<int?> StepProperty = AvaloniaProperty.Register<ProgressPanel, int?>(nameof(Step));
    public static readonly StyledProperty<int?> StepsProperty = AvaloniaProperty.Register<ProgressPanel, int?>(nameof(Steps));
    public static readonly StyledProperty<IEnumerable?> LinesProperty = AvaloniaProperty.Register<ProgressPanel, IEnumerable?>(nameof(Lines));
    public static readonly StyledProperty<string?> ErrorProperty = AvaloniaProperty.Register<ProgressPanel, string?>(nameof(Error));
    public static readonly StyledProperty<bool> IsRunningProperty = AvaloniaProperty.Register<ProgressPanel, bool>(nameof(IsRunning), true);

    public string? Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public int? Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public int? Steps { get => GetValue(StepsProperty); set => SetValue(StepsProperty, value); }
    public IEnumerable? Lines { get => GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public string? Error { get => GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
    public bool IsRunning { get => GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }

    protected override Type StyleKeyOverride => typeof(DockPanel);

    readonly TextBlock heading = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock count = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    readonly ProgressBar bar = new() { Minimum = 0, Margin = new Thickness(0, 8, 0, 0) };
    readonly SelectableTextBlock error = new() { TextWrapping = TextWrapping.Wrap };
    readonly Border errorBox;
    readonly SetupLog log = new();

    public ProgressPanel()
    {
        count.Classes.Add("muted");
        count.Classes.Add("small");
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(heading);
        Grid.SetColumn(count, 1);
        top.Children.Add(count);
        var head = new StackPanel { Children = { top, bar }, Margin = new Thickness(0, 0, 0, 12) };
        SetDock(head, Dock.Top);
        Children.Add(head);

        errorBox = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 12), Child = error, IsVisible = false };
        SetDock(errorBox, Dock.Top);
        Children.Add(errorBox);

        Children.Add(log);
        errorBox.Token(Border.BackgroundProperty, "BadBg");
        Show();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LinesProperty) log.Lines = Lines;
        else if (change.Property == HeadingProperty || change.Property == StepProperty || change.Property == StepsProperty
            || change.Property == ErrorProperty || change.Property == IsRunningProperty) Show();
    }

    void Show()
    {
        var failed = !string.IsNullOrEmpty(Error);
        heading.Text = Heading;
        count.Text = Step is { } at && Steps is { } of && of > 1
            ? failed ? $"Stopped at step {at} of {of}" : IsRunning ? $"Step {at} of {of}" : $"{of} of {of} steps"
            : null;
        count.IsVisible = count.Text is not null;
        count.Classes.Set("activityerror", failed);
        bar.IsVisible = IsRunning && !failed;
        bar.IsIndeterminate = Steps is not > 1;
        bar.Maximum = Steps ?? 1;
        // The bar stands halfway into the current step, so step 1 of 1 or 1 of 7 does not look like nothing has happened.
        bar.Value = Step is { } s ? s - 0.5 : 0;
        error.Text = Error;
        errorBox.IsVisible = failed;
    }
}
