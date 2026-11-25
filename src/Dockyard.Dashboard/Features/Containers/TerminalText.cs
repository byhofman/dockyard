using System.Text.RegularExpressions;

namespace Dockyard.Dashboard.Features.Containers;

// Output written for a terminal, as text: colours, cursor moves and window titles go, and so do other control characters
// but tabs. systemd in a kind node, for one, colours its status lines.
static partial class TerminalText
{
    public static string Plain(string text) =>
        text.IndexOf('\x1b') < 0 && !text.Any(c => char.IsControl(c) && c != '\t') ? text : Controls().Replace(Escapes().Replace(text, ""), "");

    // CSI (ESC [ … final byte), OSC (ESC ] … BEL or ESC \), and the two-byte escapes.
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[@-Z\\-_]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"[\x00-\x08\x0b-\x1f\x7f]")]
    private static partial Regex Controls();
}
