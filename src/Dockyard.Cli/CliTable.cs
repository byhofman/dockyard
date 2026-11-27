using Spectre.Console;

namespace Dockyard.Cli;

// The command's lists: columns padded to their widest cell under an upper-case header, two spaces apart. Only the last
// column, the one with sentences in it, wraps: within itself where it keeps enough room, under its row where it would not.
// Piped output has no width, so every row stays one line for grep and cut.
sealed class CliTable(params string[] headers)
{
    const string Gap = "  ";
    const string Empty = "-";
    const int NarrowestText = 30;

    readonly List<string[]> rows = [];

    public CliTable Add(params string?[] cells)
    {
        if (cells.Length != headers.Length) throw new ArgumentException($"{headers.Length} cells, one per column");
        rows.Add([.. cells.Select(c => string.IsNullOrWhiteSpace(c) ? Empty : c.Trim())]);
        return this;
    }

    public void Write(IAnsiConsole console)
    {
        // Spectre wraps what it writes at the profile's width, which is 80 when nothing says otherwise, so the lines go out as they are.
        var writer = console.Profile.Out.Writer;
        foreach (var line in Lines(System.Console.IsOutputRedirected ? null : console.Profile.Width)) writer.WriteLine(line);
        writer.Flush();
    }

    public IReadOnlyList<string> Lines(int? width)
    {
        var last = headers.Length - 1;
        var widths = Enumerable.Range(0, last).Select(i => rows.Select(r => r[i].Length).Append(headers[i].Length).Max()).ToArray();
        var indent = widths.Sum() + Gap.Length * last;
        var textWidth = width is { } w ? w - indent : (int?)null;
        var under = textWidth < NarrowestText && textWidth < rows.Select(r => r[last].Length).Append(headers[last].Length).Max();
        string Fixed(string[] cells) => string.Join(Gap, cells.Take(last).Select((c, i) => c.PadRight(widths[i])));

        List<string> lines = [under ? Fixed(headers).TrimEnd() : Fixed(headers) + Gap + headers[last]];
        foreach (var row in rows)
        {
            if (under)
            {
                var hang = new string(' ', widths[0] + Gap.Length);
                lines.Add(Fixed(row).TrimEnd());
                lines.AddRange(Wrap(row[last], width!.Value - hang.Length).Select(l => hang + l));
                continue;
            }
            var wrapped = Wrap(row[last], textWidth);
            lines.Add(Fixed(row) + Gap + wrapped[0]);
            lines.AddRange(wrapped.Skip(1).Select(l => new string(' ', indent) + l));
        }
        return lines;
    }

    static List<string> Wrap(string text, int? width)
    {
        if (width is not { } room || text.Length <= room) return [text];
        List<string> lines = [];
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0) line = word;
            else if (line.Length + 1 + word.Length <= room) line += " " + word;
            else { lines.Add(line); line = word; }
        }
        lines.Add(line);
        return lines;
    }
}
