using System.Globalization;

namespace Dockyard.Engine.Internal;

static class RfcTime
{
    // .NET parses at most 7 fractional digits; Docker sends 9.
    public static DateTimeOffset? Parse(ReadOnlySpan<char> s)
    {
        var dot = s.IndexOf('.');
        string text;
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < s.Length && char.IsAsciiDigit(s[end])) end++;
            var fraction = s[(dot + 1)..end];
            if (fraction.Length > 7) fraction = fraction[..7];
            text = $"{s[..dot]}.{fraction}{s[end..]}";
        }
        else text = s.ToString();

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }
}
