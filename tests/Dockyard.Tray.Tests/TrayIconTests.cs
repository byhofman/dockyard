using System.Text.RegularExpressions;
using Avalonia.Media;
using Dockyard.Tray;

namespace Dockyard.Tray.Tests;

public sealed partial class TrayIconTests
{
    // The brightest light and the darkest-but-one dark menu bar and taskbar it sits on (macOS, Windows 11).
    static readonly Color LightBar = Color.Parse("#F3F3F3");
    static readonly Color DarkBar = Color.Parse("#2B2D31");

    static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dockyard.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void Running_is_a_blue_from_the_logo_s_gradient()
    {
        var svg = File.ReadAllText(Path.Combine(Root(), "branding", "app-icon", "icon.svg"));
        var stops = GradientStop().Matches(svg).Select(m => Color.Parse(m.Groups["c"].Value)).ToList();
        Assert.Equal(2, stops.Count);
        var running = TrayColors.Running;
        static bool Between(byte v, byte a, byte b) => v >= Math.Min(a, b) && v <= Math.Max(a, b);
        Assert.True(Between(running.R, stops[0].R, stops[1].R) && Between(running.G, stops[0].G, stops[1].G) && Between(running.B, stops[0].B, stops[1].B),
            $"{running} is not between the logo's gradient stops {stops[0]} and {stops[1]}");
    }

    [Fact]
    public void Every_state_and_the_running_dot_stand_out_on_a_light_and_a_dark_bar()
    {
        foreach (var color in new[] { TrayColors.Running, TrayColors.Stopped, TrayColors.Busy, TrayColors.RunningDot })
            foreach (var bar in new[] { LightBar, DarkBar })
                Assert.True(Contrast(color, bar) >= 3, $"{color} on {bar} is {Contrast(color, bar):F2}:1, needs 3:1");
    }

    static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double C(byte v)
            {
                var s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C(c.R) + 0.7152 * C(c.G) + 0.0722 * C(c.B);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [GeneratedRegex(@"<stop offset=""[01]"" stop-color=""(?<c>#[0-9A-Fa-f]{6})""")]
    private static partial Regex GradientStop();
}
