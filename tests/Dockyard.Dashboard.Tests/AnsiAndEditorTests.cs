using Dockyard.Dashboard.Controls;
using Dockyard.Dashboard.Features.Containers;

namespace Dockyard.Dashboard.Tests;

public sealed class AnsiAndEditorTests
{
    [Fact]
    public void Colour_codes_become_spans_over_the_plain_text()
    {
        var (text, spans) = AnsiText.Parse("\x1b[32mINFO\x1b[0m started \x1b[1;31mfailed\x1b[22m twice\x1b[0m");

        Assert.Equal("INFO started failed twice", text);
        Assert.Equal(3, spans.Count);
        Assert.Equal((0, 4, AnsiColor.Palette(2)), (spans[0].Start, spans[0].Length, spans[0].Foreground!.Value));
        Assert.False(spans[0].Bold);
        Assert.Equal((13, 6, AnsiColor.Palette(1), true), (spans[1].Start, spans[1].Length, spans[1].Foreground!.Value, spans[1].Bold));
        Assert.Equal((19, 6, AnsiColor.Palette(1), false), (spans[2].Start, spans[2].Length, spans[2].Foreground!.Value, spans[2].Bold));
    }

    [Theory]
    [InlineData("\x1b[38;5;196mx", -1, 255, 0, 0)]
    [InlineData("\x1b[38;5;9mx", 9, 0, 0, 0)]
    [InlineData("\x1b[38;2;10;20;30mx", -1, 10, 20, 30)]
    [InlineData("\x1b[94mx", 12, 0, 0, 0)]
    public void Extended_colours_are_read(string raw, int index, int r, int g, int b)
    {
        var (_, spans) = AnsiText.Parse(raw);

        Assert.Equal(new AnsiColor(index, (byte)r, (byte)g, (byte)b), spans.Single().Foreground);
    }

    [Fact]
    public void Other_escapes_and_control_characters_go_and_plain_lines_stay_as_they_are()
    {
        Assert.Equal(("title gone", 0), Flatten(AnsiText.Parse("\x1b]0;window\x07title\x1b[2K gone")));
        Assert.Equal(("a\tb", 0), Flatten(AnsiText.Parse("a\tb\r")));
        Assert.Equal("plain", AnsiLine.From("plain").Text);
        Assert.Empty(AnsiLine.From("plain").Spans);
    }

    [Fact]
    public void Reverse_swaps_the_colours_and_a_dark_theme_gets_its_own_palette()
    {
        var span = AnsiText.Parse("\x1b[7;31mX").Spans.Single();

        Assert.Equal(AnsiColor.Palette(0), span.Foreground);
        Assert.Equal(AnsiColor.Palette(1), span.Background);
        Assert.NotEqual(AnsiTextBlock.ToColor(AnsiColor.Palette(7), dark: true), AnsiTextBlock.ToColor(AnsiColor.Palette(7), dark: false));
    }

    static (string, int) Flatten((string Text, IReadOnlyList<AnsiSpan> Spans) parsed) => (parsed.Text, parsed.Spans.Count);

    [Fact]
    public void Json_keys_strings_numbers_and_keywords_are_told_apart()
    {
        var line = """  "registry-mirrors": ["https://m"], "debug": true, "max-concurrent-downloads": 6""";

        var tokens = CodeColorizer.Tokens(CodeLanguage.Json, line).Select(t => (line.Substring(t.Start, t.Length), t.Token)).ToList();

        Assert.Equal(
        [
            ("\"registry-mirrors\":", CodeColorizer.Token.Key), ("\"https://m\"", CodeColorizer.Token.String),
            ("\"debug\":", CodeColorizer.Token.Key), ("true", CodeColorizer.Token.Keyword),
            ("\"max-concurrent-downloads\":", CodeColorizer.Token.Key), ("6", CodeColorizer.Token.Number),
        ], tokens);
    }

    [Fact]
    public void Yaml_keys_values_and_comments_are_told_apart()
    {
        static List<(string, CodeColorizer.Token)> Of(string line) =>
            CodeColorizer.Tokens(CodeLanguage.Yaml, line).Select(t => (line.Substring(t.Start, t.Length), t.Token)).ToList();

        Assert.Equal([("kind", CodeColorizer.Token.Key)], Of("kind: Cluster"));
        Assert.Equal([("role", CodeColorizer.Token.Key)], Of("- role: control-plane"));
        Assert.Equal([("containerPort", CodeColorizer.Token.Key), ("80", CodeColorizer.Token.Number), ("# http", CodeColorizer.Token.Comment)], Of("  containerPort: 80 # http"));
        Assert.Equal([("name", CodeColorizer.Token.Key), ("\"a # b\"", CodeColorizer.Token.String)], Of("name: \"a # b\""));
        Assert.Equal([("# whole line", CodeColorizer.Token.Comment)], Of("# whole line"));
    }
}
