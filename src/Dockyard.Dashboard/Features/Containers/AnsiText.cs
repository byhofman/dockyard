using System.Text;
using System.Text.RegularExpressions;

namespace Dockyard.Dashboard.Features.Containers;

// A colour from a program's output: one of the 16 terminal colours, which follow the theme, or an exact one.
readonly record struct AnsiColor(int Index, byte R = 0, byte G = 0, byte B = 0)
{
    public bool IsPalette => Index >= 0;
    public static AnsiColor Palette(int index) => new(index);
    public static AnsiColor Rgb(byte r, byte g, byte b) => new(-1, r, g, b);
}

sealed record AnsiSpan(int Start, int Length, AnsiColor? Foreground, AnsiColor? Background, bool Bold, bool Dim, bool Italic, bool Underline);

// SGR escapes (ESC [ … m) become spans over the plain text; every other escape and control character goes, as in TerminalText.
static partial class AnsiText
{
    public static (string Text, IReadOnlyList<AnsiSpan> Spans) Parse(string raw)
    {
        if (raw.IndexOf('\x1b') < 0) return (TerminalText.Plain(raw), []);
        var text = new StringBuilder(raw.Length);
        List<AnsiSpan> spans = [];
        var style = Style.None;
        var start = 0;

        void Close()
        {
            if (text.Length > start && !style.IsPlain) spans.Add(style.Span(start, text.Length - start));
            start = text.Length;
        }

        var at = 0;
        foreach (Match m in Escape().Matches(raw))
        {
            Append(text, raw.AsSpan(at, m.Index - at));
            at = m.Index + m.Length;
            if (!m.Groups["sgr"].Success) continue;
            Close();
            style = style.Apply(m.Groups["sgr"].Value);
        }
        Append(text, raw.AsSpan(at));
        Close();
        return (text.ToString(), spans);
    }

    static void Append(StringBuilder text, ReadOnlySpan<char> part)
    {
        foreach (var c in part)
            if (c == '\t' || !char.IsControl(c)) text.Append(c);
    }

    readonly record struct Style(AnsiColor? Foreground, AnsiColor? Background, bool Bold, bool Dim, bool Italic, bool Underline, bool Reverse)
    {
        public static readonly Style None = new(null, null, false, false, false, false, false);

        public bool IsPlain => this == None;

        public AnsiSpan Span(int start, int length) => Reverse
            ? new(start, length, Background ?? AnsiColor.Palette(0), Foreground ?? AnsiColor.Palette(7), Bold, Dim, Italic, Underline)
            : new(start, length, Foreground, Background, Bold, Dim, Italic, Underline);

        public Style Apply(string parameters)
        {
            var codes = parameters.Length == 0 ? [0] : parameters.Split(';', ':').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var s = this;
            for (var i = 0; i < codes.Length; i++)
            {
                var code = codes[i];
                switch (code)
                {
                    case 0: s = None; break;
                    case 1: s = s with { Bold = true }; break;
                    case 2: s = s with { Dim = true }; break;
                    case 3: s = s with { Italic = true }; break;
                    case 4: s = s with { Underline = true }; break;
                    case 7: s = s with { Reverse = true }; break;
                    case 22: s = s with { Bold = false, Dim = false }; break;
                    case 23: s = s with { Italic = false }; break;
                    case 24: s = s with { Underline = false }; break;
                    case 27: s = s with { Reverse = false }; break;
                    case >= 30 and <= 37: s = s with { Foreground = AnsiColor.Palette(code - 30) }; break;
                    case 39: s = s with { Foreground = null }; break;
                    case >= 40 and <= 47: s = s with { Background = AnsiColor.Palette(code - 40) }; break;
                    case 49: s = s with { Background = null }; break;
                    case >= 90 and <= 97: s = s with { Foreground = AnsiColor.Palette(code - 90 + 8) }; break;
                    case >= 100 and <= 107: s = s with { Background = AnsiColor.Palette(code - 100 + 8) }; break;
                    case 38 or 48:
                        var color = Extended(codes, ref i);
                        s = code == 38 ? s with { Foreground = color ?? s.Foreground } : s with { Background = color ?? s.Background };
                        break;
                }
            }
            return s;
        }

        // 5;n picks from the 256 colours, 2;r;g;b is exact.
        static AnsiColor? Extended(int[] codes, ref int i)
        {
            if (i + 2 < codes.Length && codes[i + 1] == 5)
            {
                var n = codes[i + 2];
                i += 2;
                return n is >= 0 and < 16 ? AnsiColor.Palette(n) : n is >= 16 and < 256 ? FromCube(n) : null;
            }
            if (i + 4 < codes.Length && codes[i + 1] == 2)
            {
                var color = AnsiColor.Rgb((byte)codes[i + 2], (byte)codes[i + 3], (byte)codes[i + 4]);
                i += 4;
                return color;
            }
            return null;
        }

        static AnsiColor FromCube(int n)
        {
            if (n >= 232)
            {
                var gray = (byte)(8 + (n - 232) * 10);
                return AnsiColor.Rgb(gray, gray, gray);
            }
            n -= 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return AnsiColor.Rgb(Level(n / 36), Level(n / 6 % 6), Level(n % 6));
        }
    }

    // SGR first, then the escapes TerminalText drops.
    [GeneratedRegex(@"\x1b\[(?<sgr>[0-9;:]*)m|\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]")]
    private static partial Regex Escape();
}

sealed record AnsiLine(string Text, IReadOnlyList<AnsiSpan> Spans)
{
    public static AnsiLine From(string raw)
    {
        var (text, spans) = AnsiText.Parse(raw);
        return new(text, spans);
    }
}
