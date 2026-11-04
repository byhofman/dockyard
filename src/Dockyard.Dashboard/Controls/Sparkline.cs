using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Dockyard.Dashboard.Controls;

sealed class Sparkline : Control
{
    public static readonly StyledProperty<double[]?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, double[]?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.DodgerBlue);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum));

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, MaximumProperty);

    public double[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    const int Slots = 60;

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var size = Bounds.Size;
        if (values is not { Length: > 1 } || size.Width <= 0 || size.Height <= 0) return;

        var max = Maximum > 0 ? Maximum : Math.Max(values.Max() * 1.15, 1e-9);
        var step = size.Width / (Slots - 1);
        var start = (Slots - values.Length) * step;
        double Y(double v) => size.Height - Math.Clamp(v / max, 0, 1) * (size.Height - 2) - 1;

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var g = line.Open())
        using (var f = fill.Open())
        {
            g.BeginFigure(new Point(start, Y(values[0])), false);
            f.BeginFigure(new Point(start, size.Height), true);
            f.LineTo(new Point(start, Y(values[0])));
            for (var i = 1; i < values.Length; i++)
            {
                var p = new Point(start + i * step, Y(values[i]));
                g.LineTo(p);
                f.LineTo(p);
            }
            f.LineTo(new Point(start + (values.Length - 1) * step, size.Height));
            g.EndFigure(false);
            f.EndFigure(true);
        }

        if (Stroke is ISolidColorBrush solid)
            context.DrawGeometry(new SolidColorBrush(solid.Color, 0.15), null, fill);
        context.DrawGeometry(null, new Pen(Stroke, 1.5), line);
    }
}
