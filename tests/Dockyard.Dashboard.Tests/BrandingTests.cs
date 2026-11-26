using System.Text.RegularExpressions;

namespace Dockyard.Dashboard.Tests;

// The logo's colours carry the interface's accent, every text token keeps WCAG AA in both themes, and colours live only in
// the dashboard's theme tokens, so a new logo colour or a stray literal cannot leave the interface behind.
public sealed partial class BrandingTests
{
    static readonly string[] Apps = ["src/Dockyard.Dashboard/App.axaml"];

    public static TheoryData<string, string> AppsAndThemes => new()
    {
        { Apps[0], "Light" }, { Apps[0], "Dark" },
    };

    static string Root([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Dockyard.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative));

    static string Icon(string file) => Read(Path.Combine("branding", "app-icon", file));

    static string LogoBlue() => GradientStop().Matches(Icon("icon.svg")).Single(m => m.Groups["o"].Value == "1").Groups["c"].Value.ToUpperInvariant();

    static string LogoLightBlue() => GradientStop().Matches(Icon("icon.svg")).Single(m => m.Groups["o"].Value == "0").Groups["c"].Value.ToUpperInvariant();

    static string Crane(string file) => CraneFill().Match(Icon(file)).Groups["c"].Value.ToUpperInvariant();

    static Dictionary<string, string> Tokens(string app, string theme)
    {
        var xaml = Read(app);
        var themed = ThemeDictionary().Matches(xaml).Single(m => m.Groups["t"].Value == theme).Groups["body"].Value;
        var shared = xaml[xaml.IndexOf("</ResourceDictionary.ThemeDictionaries>", StringComparison.Ordinal)..xaml.IndexOf("</Application.Resources>", StringComparison.Ordinal)];
        return BrushToken().Matches(shared).Concat(BrushToken().Matches(themed))
            .ToDictionary(m => m.Groups["k"].Value, m => m.Groups["c"].Value.ToUpperInvariant());
    }

    [Theory]
    [InlineData("src/Dockyard.Dashboard/App.axaml")]
    public void The_accent_is_the_logo_s_blue(string app)
    {
        var xaml = Read(app);
        Assert.Equal("#1B74C9", LogoBlue());
        Assert.Equal(LogoBlue(), Tokens(app, "Light")["Accent"]);
        Assert.Equal(LogoBlue(), SystemAccent().Match(xaml).Groups["c"].Value.ToUpperInvariant());
    }

    [Theory]
    [InlineData("src/Dockyard.Dashboard/App.axaml")]
    public void Links_brand_and_selection_take_the_logo_s_colours(string app)
    {
        var (light, dark) = (Tokens(app, "Light"), Tokens(app, "Dark"));
        Assert.Equal(LogoBlue(), light["Brand"]);
        Assert.Equal(LogoLightBlue(), dark["Brand"]);
        Assert.Equal(LogoLightBlue(), dark["LinkText"]);
        Assert.Equal(Crane("icon-light.svg"), light["SelectedText"]);
        Assert.Equal(Crane("icon.svg"), dark["SelectedText"]);
    }

    [Theory]
    [InlineData("src/Dockyard.Dashboard/App.axaml")]
    public void Both_themes_follow_the_system_and_define_the_same_tokens(string app)
    {
        Assert.Contains("RequestedThemeVariant=\"Default\"", Read(app), StringComparison.Ordinal);
        Assert.Equal(Tokens(app, "Light").Keys.Order(), Tokens(app, "Dark").Keys.Order());
    }

    [Theory]
    [MemberData(nameof(AppsAndThemes))]
    public void Text_meets_AA_on_every_surface_it_sits_on(string app, string theme)
    {
        var t = Tokens(app, theme);
        foreach (var fg in new[] { "Text", "Muted" })
            foreach (var bg in new[] { "Bg", "Surface", "SurfaceRaised", "Hover", "Selected", "PillBg", "TextControlBackground" })
                AssertContrast(t, fg, bg, 4.5);
        foreach (var bg in new[] { "Bg", "Surface", "Selected", "Hover" }) AssertContrast(t, "LinkText", bg, 4.5);
        foreach (var bg in new[] { "Surface", "SurfaceRaised", "BadBg" }) AssertContrast(t, "Bad", bg, 4.5);
        foreach (var bg in new[] { "Accent", "AccentHover", "DangerFill", "DangerFillHover" }) AssertContrast(t, "OnAccent", bg, 4.5);
        AssertContrast(t, "SelectedText", "Selected", 4.5);
        AssertContrast(t, "AccentText", "AccentBg", 4.5);
        AssertContrast(t, "Ok", "OkBg", 4.5);
        AssertContrast(t, "Warn", "WarnBg", 4.5);
        AssertContrast(t, "Muted", "OkBg", 4.5);
        AssertContrast(t, "TextControlPlaceholderForeground", "TextControlBackground", 4.5);
    }

    [Theory]
    [MemberData(nameof(AppsAndThemes))]
    public void Icons_and_dots_meet_the_non_text_minimum(string app, string theme)
    {
        var t = Tokens(app, theme);
        foreach (var bg in new[] { "Bg", "Surface", "Hover", "Selected" })
        {
            AssertContrast(t, "Brand", bg, 3);
            AssertContrast(t, "Ok", bg, 3);
            AssertContrast(t, "Muted", bg, 3);
        }
    }

    [Fact]
    public void Views_use_tokens_not_colour_literals()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Root(), "src"), "*.axaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var text = TokenDeclaration().Replace(File.ReadAllText(file), "");
            var literals = ColourLiteral().Matches(text).Select(m => m.Value).ToList();
            Assert.True(literals.Count == 0, $"{Path.GetRelativePath(Root(), file)} has colour literals outside the theme tokens: {string.Join(", ", literals)}");
        }
    }

    static void AssertContrast(Dictionary<string, string> tokens, string fg, string bg, double minimum)
    {
        var ratio = Contrast(tokens[fg], tokens[bg]);
        Assert.True(ratio >= minimum, $"{fg} {tokens[fg]} on {bg} {tokens[bg]} is {ratio:F2}:1, needs {minimum}:1");
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

    [GeneratedRegex(@"<stop offset=""(?<o>[01])"" stop-color=""(?<c>#[0-9A-Fa-f]{6})""")]
    private static partial Regex GradientStop();

    [GeneratedRegex(@"<rect x=""28"" y=""22""[^>]*fill=""(?<c>#[0-9A-Fa-f]{6})""")]
    private static partial Regex CraneFill();

    [GeneratedRegex(@"<ResourceDictionary x:Key=""(?<t>Light|Dark)"">(?<body>.*?)</ResourceDictionary>", RegexOptions.Singleline)]
    private static partial Regex ThemeDictionary();

    [GeneratedRegex(@"<SolidColorBrush x:Key=""(?<k>\w+)"" Color=""(?<c>#[0-9A-Fa-f]{6})"" />")]
    private static partial Regex BrushToken();

    [GeneratedRegex(@"x:Key=""SystemAccentColor"">(?<c>#[0-9A-Fa-f]{6})<")]
    private static partial Regex SystemAccent();

    [GeneratedRegex(@"<SolidColorBrush x:Key=""\w+"" Color=""#[0-9A-Fa-f]{6}"" />|<Color x:Key=""\w+"">#[0-9A-Fa-f]{6}</Color>")]
    private static partial Regex TokenDeclaration();

    [GeneratedRegex(@"[""'>]#[0-9A-Fa-f]{3,8}\b|=""(White|Black|Red|Green|Blue|Gray|Grey|DodgerBlue|Orange|Yellow)""")]
    private static partial Regex ColourLiteral();
}
