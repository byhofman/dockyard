namespace Dockyard.Core;

// ~\.wslconfig belongs to the user and applies to all their distros, so only an unset value is filled in, and
// the line is marked so that uninstalling takes back exactly what Dockyard added.
static class WslConfig
{
    public const string MirroredLine = "networkingMode=mirrored # added by Dockyard";

    public static string? WithMirroredNetworking(string? existing)
    {
        var text = (existing ?? "").ReplaceLineEndings("\n");
        var lines = text.Split('\n').ToList();
        if (lines.Any(IsNetworkingMode)) return null;

        var section = lines.FindIndex(l => l.Trim().Equals("[wsl2]", StringComparison.OrdinalIgnoreCase));
        if (section >= 0) lines.Insert(section + 1, MirroredLine);
        else
        {
            if (text.Length > 0 && !text.EndsWith('\n')) lines.Add("");
            if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            lines.AddRange(["[wsl2]", MirroredLine, ""]);
        }
        return string.Join('\n', lines);
    }

    public static string? WithoutDockyardLine(string? existing)
    {
        if (existing is null) return null;
        var lines = existing.ReplaceLineEndings("\n").Split('\n').ToList();
        return lines.Remove(MirroredLine) ? string.Join('\n', lines) : null;
    }

    static bool IsNetworkingMode(string line)
    {
        var key = line.Split('=', 2)[0].Trim();
        return !line.TrimStart().StartsWith('#') && key.Equals("networkingMode", StringComparison.OrdinalIgnoreCase);
    }
}
