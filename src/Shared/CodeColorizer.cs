using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace Dockyard.Shared;

enum CodeLanguage { Json, Yaml }

// Line by line, with the dashboard's own theme colours: daemon.json and kind's cluster files are short and need no more
// than keys, strings, numbers, keywords and comments told apart.
sealed partial class CodeColorizer(CodeLanguage language, Func<string, IBrush?> brush) : DocumentColorizingTransformer
{
    public enum Token { Key, String, Number, Keyword, Comment }

    public static string ThemeKey(Token token) => token switch
    {
        Token.Key => "AccentText",
        Token.String => "Ok",
        Token.Number or Token.Keyword => "Warn",
        _ => "Muted",
    };

    public static IEnumerable<(int Start, int Length, Token Token)> Tokens(CodeLanguage language, string line) =>
        language == CodeLanguage.Json ? JsonTokens(line) : YamlTokens(line);

    static IEnumerable<(int, int, Token)> JsonTokens(string line)
    {
        foreach (Match m in JsonToken().Matches(line))
        {
            if (m.Groups["string"].Success)
                yield return (m.Index, m.Length, m.Groups["colon"].Success ? Token.Key : Token.String);
            else if (m.Groups["number"].Success) yield return (m.Index, m.Length, Token.Number);
            else yield return (m.Index, m.Length, Token.Keyword);
        }
    }

    static IEnumerable<(int, int, Token)> YamlTokens(string line)
    {
        var comment = CommentStart(line);
        var code = comment < 0 ? line : line[..comment];
        if (YamlKey().Match(code) is { Success: true } key) yield return (key.Groups["key"].Index, key.Groups["key"].Length, Token.Key);
        foreach (Match m in YamlScalar().Matches(code))
        {
            if (m.Groups["string"].Success) yield return (m.Index, m.Length, Token.String);
            else if (m.Groups["number"].Success) yield return (m.Index, m.Length, Token.Number);
            else yield return (m.Index, m.Length, Token.Keyword);
        }
        if (comment >= 0) yield return (comment, line.Length - comment, Token.Comment);
    }

    // A # starts a comment at the start of a line or after a space, outside quotes.
    static int CommentStart(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return i;
        }
        return -1;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        var text = CurrentContext.Document.GetText(line);
        foreach (var (start, length, token) in Tokens(language, text))
        {
            if (brush(ThemeKey(token)) is not { } b) continue;
            ChangeLinePart(line.Offset + start, line.Offset + start + length, e => e.TextRunProperties.SetForegroundBrush(b));
        }
    }

    [GeneratedRegex("""(?<string>"(?:[^"\\]|\\.)*")(?<colon>\s*:)?|(?<number>-?\b\d+(?:\.\d+)?(?:[eE][+-]?\d+)?\b)|\b(?:true|false|null)\b""")]
    private static partial Regex JsonToken();

    [GeneratedRegex("""^\s*(?:-\s+)?(?<key>[^\s:#'"\-][^:#]*?|"[^"]*"|'[^']*')\s*:(?:\s|$)""")]
    private static partial Regex YamlKey();

    [GeneratedRegex("""(?<string>"(?:[^"\\]|\\.)*"|'[^']*')|(?<=[:\-\[,]\s*)(?:(?<number>-?\d+(?:\.\d+)?)|(?:true|false|null|yes|no|on|off))(?=\s*(?:[,\]]|$))""")]
    private static partial Regex YamlScalar();
}
