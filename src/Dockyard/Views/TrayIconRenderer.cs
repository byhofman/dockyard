using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Dockyard.Views;

// The app icon's crane and container (branding/app-icon/icon.svg, 100x100 units) tinted by engine state, with a context badge.
static class TrayIconRenderer
{
    const int Size = 64;
    static readonly Geometry Mark = Geometry.Parse(
        "F0 M31 22H69A3 3 0 0 1 69 28H31A3 3 0 0 1 31 22Z M48.7 28H51.3V39H48.7Z " +
        "M45.25 39H54.75A2.25 2.25 0 0 1 54.75 43.5H45.25A2.25 2.25 0 0 1 45.25 39Z " +
        "M30 46H70A4 4 0 0 1 74 50V69A4 4 0 0 1 70 73H30A4 4 0 0 1 26 69V50A4 4 0 0 1 30 46Z " +
        string.Concat(new[] { 35, 43, 51, 59, 67 }.Select(x => FormattableString.Invariant($"M{x - 1.2} 56A1.2 1.2 0 0 1 {x + 1.2} 56V67A1.2 1.2 0 0 1 {x - 1.2} 67Z "))));
    static readonly Matrix MarkToIcon =
        Matrix.CreateTranslation(-50, -47.5) * Matrix.CreateScale(Size / 54.0, Size / 54.0) * Matrix.CreateTranslation(Size / 2.0, Size / 2.0);
    static readonly Dictionary<(Color, string), WindowIcon> Cache = [];

    public static readonly Color Running = Color.Parse("#2496ED");
    public static readonly Color Stopped = Color.Parse("#8A8A8A");
    public static readonly Color Busy = Color.Parse("#E8A317");
    static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#4B5563"));

    public static WindowIcon Render(Color mark, string? badge)
    {
        if (Cache.TryGetValue((mark, badge ?? ""), out var cached)) return cached;

        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var bounds = new Rect(0, 0, Size, Size);
            var d = Size * 0.56;
            var badgeRect = new Rect(Size - d, Size - d, d, d);
            var cutout = badgeRect.Inflate(Size / 16.0);

            Geometry clip = badge is null ? new RectangleGeometry(bounds) : new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new EllipseGeometry(cutout));
            using (ctx.PushGeometryClip(clip))
            using (ctx.PushTransform(MarkToIcon))
                ctx.DrawGeometry(new SolidColorBrush(mark), null, Mark);

            if (badge is not null)
            {
                ctx.DrawEllipse(BadgeBrush, null, badgeRect);
                var text = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), d * 0.78, Brushes.White);
                ctx.DrawText(text, new Point(badgeRect.Center.X - text.Width / 2, badgeRect.Center.Y - text.Height / 2));
            }
        }

        var icon = new WindowIcon(bitmap);
        Cache[(mark, badge ?? "")] = icon;
        return icon;
    }
}
