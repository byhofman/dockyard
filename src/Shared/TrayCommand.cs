namespace Dockyard.Shared;

readonly record struct TrayCommand(TrayCommandKind Kind, string? Engine = null)
{
    public static readonly string[] Engines = ["linux", "win"];

    public override string ToString() =>
        Engine is null ? Kind.ToString().ToLowerInvariant() : $"{Kind.ToString().ToLowerInvariant()} {Engine}";

    public static TrayCommand? Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts switch
        {
            ["show"] => new TrayCommand(TrayCommandKind.Show),
            ["settings"] => new TrayCommand(TrayCommandKind.Settings),
            ["start", var e] when Engines.Contains(e) => new TrayCommand(TrayCommandKind.Start, e),
            ["switch", var e] when Engines.Contains(e) => new TrayCommand(TrayCommandKind.Switch, e),
            _ => null,
        };
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
