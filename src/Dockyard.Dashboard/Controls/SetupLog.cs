using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dockyard.Setup;

namespace Dockyard.Dashboard.Controls;

// The lines a setup run reported, in the activity log's type: steps semibold, commands muted and smaller, warnings and errors
// in their colours. Following, it keeps the newest line in view in a list of its own height; otherwise it is as tall as
// its lines, for a few of them inside a page that scrolls.
sealed class SetupLog : Border
{
    public static readonly StyledProperty<IEnumerable?> LinesProperty = AvaloniaProperty.Register<SetupLog, IEnumerable?>(nameof(Lines));
    public static readonly StyledProperty<bool> FollowsProperty = AvaloniaProperty.Register<SetupLog, bool>(nameof(Follows), true);

    public IEnumerable? Lines { get => GetValue(LinesProperty); set => SetValue(LinesProperty, value); }
    public bool Follows { get => GetValue(FollowsProperty); set => SetValue(FollowsProperty, value); }

    protected override Type StyleKeyOverride => typeof(Border);

    public static IDataTemplate LineTemplate { get; } = new FuncDataTemplate<SetupProgress?>((line, _) =>
    {
        // A recycled container is given no line.
        if (line is null) return new SelectableTextBlock();
        var text = new SelectableTextBlock { Text = line.Line, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Classes.Add("activity");
        text.Classes.Add("console");
        text.Classes.Set("step", line.Step is not null);
        text.Classes.Set("command", line.Kind == SetupProgressKind.Command);
        text.Classes.Set("warning", line.Kind == SetupProgressKind.Warning);
        text.Classes.Set("error", line.Kind == SetupProgressKind.Error);
        if (line.Step is not null) text.Margin = new Thickness(0, 6, 0, 1);
        return text;
    });

    readonly FollowingLogList list = new() { FollowsItems = true, Padding = new Thickness(0, 6), ItemTemplate = LineTemplate };
    readonly ItemsControl inline = new() { Padding = new Thickness(12, 8), ItemTemplate = LineTemplate };

    public SetupLog()
    {
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(6);
        ClipToBounds = true;
        list.Classes.Add("log");
        this.Token(BackgroundProperty, "Bg").Token(BorderBrushProperty, "Line");
        Child = list;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FollowsProperty) Child = Follows ? list : inline;
        if (change.Property == LinesProperty || change.Property == FollowsProperty)
        {
            list.ItemsSource = Follows ? Lines : null;
            inline.ItemsSource = Follows ? null : Lines;
        }
    }
}
