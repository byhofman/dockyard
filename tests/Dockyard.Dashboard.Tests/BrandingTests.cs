using System.Text.RegularExpressions;

namespace Dockyard.Dashboard.Tests;

// The interface's one hint of the brand is the blue of the logo's container; this keeps the two from drifting apart.
public sealed partial class BrandingTests
{
    static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dockyard.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    static string LogoBlue()
    {
        var svg = File.ReadAllText(Path.Combine(Root(), "branding", "app-icon", "icon.svg"));
        return GradientEnd().Match(svg).Groups["c"].Value.ToUpperInvariant();
    }

    [Theory]
    [InlineData("src/Dockyard/App.axaml")]
    [InlineData("src/Dockyard.Dashboard/App.axaml")]
    public void The_accent_is_the_logo_s_blue(string app)
    {
        var xaml = File.ReadAllText(Path.Combine(Root(), app));
        Assert.Equal("#1B74C9", LogoBlue());
        Assert.Equal(LogoBlue(), Accent().Match(xaml).Groups["c"].Value.ToUpperInvariant());
        Assert.Equal(LogoBlue(), SystemAccent().Match(xaml).Groups["c"].Value.ToUpperInvariant());
    }

    // White on the accent is what primary buttons show; it has to stay readable (WCAG AA, 4.5:1).
    [Fact]
    public void White_text_on_the_accent_is_readable() => Assert.True(Contrast("#FFFFFF", LogoBlue()) >= 4.5);

    static double Contrast(string a, string b)
    {
        static double L(string hex)
        {
            double C(int i)
            {
                var v = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0;
                return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * C(0) + 0.7152 * C(1) + 0.0722 * C(2);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    [GeneratedRegex(@"<stop offset=""1"" stop-color=""(?<c>#[0-9A-Fa-f]{6})""")]
    private static partial Regex GradientEnd();

    [GeneratedRegex(@"x:Key=""Accent"" Color=""(?<c>#[0-9A-Fa-f]{6})""")]
    private static partial Regex Accent();

    [GeneratedRegex(@"x:Key=""SystemAccentColor"">(?<c>#[0-9A-Fa-f]{6})<")]
    private static partial Regex SystemAccent();
}
