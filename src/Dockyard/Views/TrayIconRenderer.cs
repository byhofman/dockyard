using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Dockyard.Views;

// The Docker whale tinted by engine state (the original is black, invisible on a dark taskbar) with a context badge.
static class TrayIconRenderer
{
    const int Size = 64;
    static readonly Bitmap Whale = new(AssetLoader.Open(new Uri("avares://Dockyard/Assets/whale.png")));
    static readonly Dictionary<(Color, string), WindowIcon> Cache = [];

    public static readonly Color Running = Color.Parse("#2496ED");
    public static readonly Color Stopped = Color.Parse("#8A8A8A");
    public static readonly Color Busy = Color.Parse("#E8A317");
    static readonly IBrush BadgeBrush = new SolidColorBrush(Color.Parse("#4B5563"));

    public static WindowIcon Render(Color whale, string badge)
    {
        if (Cache.TryGetValue((whale, badge), out var cached)) return cached;

        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            var bounds = new Rect(0, 0, Size, Size);
            var d = Size * 0.56;
            var badgeRect = new Rect(Size - d, Size - d, d, d);
            var cutout = badgeRect.Inflate(Size / 16.0);

            using (ctx.PushGeometryClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new EllipseGeometry(cutout))))
            using (ctx.PushOpacityMask(new ImageBrush(Whale) { Stretch = Stretch.Uniform }, bounds))
                ctx.FillRectangle(new SolidColorBrush(whale), bounds);

            ctx.DrawEllipse(BadgeBrush, null, badgeRect);
            var text = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI", FontStyle.Normal, FontWeight.Bold), d * 0.78, Brushes.White);
            ctx.DrawText(text, new Point(badgeRect.Center.X - text.Width / 2, badgeRect.Center.Y - text.Height / 2));
        }

        var icon = new WindowIcon(bitmap);
        Cache[(whale, badge)] = icon;
        return icon;
    }
}
