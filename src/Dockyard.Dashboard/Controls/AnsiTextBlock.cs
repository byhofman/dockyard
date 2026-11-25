using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Styling;
using Dockyard.Dashboard.Features.Containers;

namespace Dockyard.Dashboard.Controls;

// A log line with the colours its program gave it, still selectable as plain text.
sealed class AnsiTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<AnsiLine?> LineProperty = AvaloniaProperty.Register<AnsiTextBlock, AnsiLine?>(nameof(Line));

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public AnsiLine? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }

    public AnsiTextBlock() => ActualThemeVariantChanged += (_, _) => Build();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty) Build();
    }

    void Build()
    {
        var text = Line?.Text ?? "";
        if (Line?.Spans is not { Count: > 0 } spans)
        {
            Inlines = null;
            Text = text;
            return;
        }
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var inlines = new InlineCollection();
        var at = 0;
        foreach (var span in spans)
        {
            if (span.Start > at) inlines.Add(new Run(text[at..span.Start]));
            var end = Math.Min(text.Length, span.Start + span.Length);
            if (end <= span.Start) continue;
            var run = new Run(text[span.Start..end]);
            if (span.Foreground is { } fg) run.Foreground = new SolidColorBrush(ToColor(fg, dark));
            if (span.Background is { } bg) run.Background = new SolidColorBrush(ToColor(bg, dark));
            if (span.Bold) run.FontWeight = FontWeight.Bold;
            if (span.Italic) run.FontStyle = FontStyle.Italic;
            if (span.Underline) run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
            if (span.Dim && span.Foreground is null) run.Foreground = Brushes.Gray;
            inlines.Add(run);
            at = end;
        }
        if (at < text.Length) inlines.Add(new Run(text[at..]));
        Inlines = inlines;
    }

    // The 16 colours as VS Code's terminal has them, one set for each theme so that black and white stay readable.
    static readonly uint[] DarkPalette =
    [
        0xFF000000, 0xFFCD3131, 0xFF0DBC79, 0xFFE5E510, 0xFF2472C8, 0xFFBC3FBC, 0xFF11A8CD, 0xFFE5E5E5,
        0xFF666666, 0xFFF14C4C, 0xFF23D18B, 0xFFF5F543, 0xFF3B8EEA, 0xFFD670D6, 0xFF29B8DB, 0xFFE5E5E5,
    ];

    static readonly uint[] LightPalette =
    [
        0xFF000000, 0xFFCD3131, 0xFF00BC00, 0xFF949800, 0xFF0451A5, 0xFFBC05BC, 0xFF0598BC, 0xFF555555,
        0xFF666666, 0xFFCD3131, 0xFF14CE14, 0xFFB5BA00, 0xFF0451A5, 0xFFBC05BC, 0xFF0598BC, 0xFFA5A5A5,
    ];

    public static Color ToColor(AnsiColor color, bool dark) => color.IsPalette
        ? Color.FromUInt32((dark ? DarkPalette : LightPalette)[color.Index & 15])
        : Color.FromRgb(color.R, color.G, color.B);
}
