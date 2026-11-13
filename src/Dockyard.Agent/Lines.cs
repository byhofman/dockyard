using System.Text;

namespace Dockyard.Agent;

static class Lines
{
    // Nothing the agent is asked comes near this; a longer line ends the connection instead of growing without end.
    public const int Max = 64 * 1024;

    public static async Task<string?> Read(StreamReader reader, CancellationToken ct)
    {
        var line = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one, ct) == 1)
        {
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == Max) throw new IOException($"a line longer than {Max} characters");
            line.Append(one[0]);
        }
        return line.Length > 0 ? line.ToString() : null;
    }
}
