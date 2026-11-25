using System.Text;

namespace Dockyard.Shared;

// Assets: the folder setup was given with --assets, handed to the installed app with Settings; the rest of the line, spaces and all.
// Run: a dockyard command for the tray to carry out, reporting on the pipe named Reply.
readonly record struct TrayCommand(TrayCommandKind Kind, string? Engine = null, string? Assets = null, string? Reply = null, IReadOnlyList<string>? Args = null)
{
    public static readonly string[] Engines = ["linux", "win"];

    public override string ToString() =>
        Kind == TrayCommandKind.Run ? $"run {Reply} {Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\n', Args ?? [])))}"
        : Assets is not null ? $"settings {Assets}"
        : Engine is null ? Kind.ToString().ToLowerInvariant() : $"{Kind.ToString().ToLowerInvariant()} {Engine}";

    public static TrayCommand? Parse(string line)
    {
        if (line.StartsWith("settings ", StringComparison.Ordinal) && line[9..].Trim() is { Length: > 0 } folder && Path.IsPathFullyQualified(folder))
            return new TrayCommand(TrayCommandKind.Settings, Assets: folder);
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts switch
        {
            ["show"] => new TrayCommand(TrayCommandKind.Show),
            ["settings"] => new TrayCommand(TrayCommandKind.Settings),
            ["start", var e] when Engines.Contains(e) => new TrayCommand(TrayCommandKind.Start, e),
            ["switch", var e] when Engines.Contains(e) => new TrayCommand(TrayCommandKind.Switch, e),
            ["run", var reply, var args] when LocalPipe.IsReplyName(reply) && Decode(args) is { } decoded =>
                new TrayCommand(TrayCommandKind.Run, Reply: reply, Args: decoded),
            _ => null,
        };
    }

    static string[]? Decode(string base64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)).Split('\n'); }
        catch (FormatException) { return null; }
    }

    public static TrayCommand FromArgs(string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        if (args.Contains("--settings")) return new TrayCommand(TrayCommandKind.Settings);
        if (Opt("--start") is { } s && Engines.Contains(s)) return new TrayCommand(TrayCommandKind.Start, s);
        if (Opt("--switch") is { } w && Engines.Contains(w)) return new TrayCommand(TrayCommandKind.Switch, w);
        return new TrayCommand(TrayCommandKind.Show);
    }
}
