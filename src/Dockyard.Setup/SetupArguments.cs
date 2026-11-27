using Dockyard.Core;

namespace Dockyard.Setup;

// The one reading of setup's command line, for the CLI and for the dashboard's silent install. Problems come back as
// messages and the caller exits with DockyardSetup.WrongArguments.
public static class SetupArguments
{
    static readonly HashSet<string> Switches = ["--silent", "--trust-https", "--kubectl-shell", "--resume", "--no-start", "--open-dashboard", "--remove-data"];
    static readonly HashSet<string> Valued = ["--with", "--without", "--source", "--image", "--assets"];

    public static SetupRequest Parse(IReadOnlyList<string> args, out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        var flags = new HashSet<string>();
        var with = new List<string>();
        var without = new List<string>();
        string? source = null, image = null, assets = null;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            string? inline = null;
            if (name.StartsWith("--", StringComparison.Ordinal) && name.IndexOf('=') is var eq and > 0)
                (name, inline) = (name[..eq], name[(eq + 1)..]);

            if (Switches.Contains(name))
            {
                if (inline is not null) found.Add($"{name} takes no value");
                else flags.Add(name);
            }
            else if (Valued.Contains(name))
            {
                var value = inline ?? (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null);
                if (value is null) { found.Add($"{name} needs a value"); continue; }
                switch (name)
                {
                    case "--with": with.AddRange(Split(value)); break;
                    case "--without": without.AddRange(Split(value)); break;
                    case "--source": source = value; break;
                    case "--image": image = value; break;
                    default: assets = value; break;
                }
            }
            else found.Add(name.StartsWith("--", StringComparison.Ordinal) ? $"Unknown option {name}" : $"Unexpected argument {name}");
        }

        var toolSource = ToolSourceArgument.Parse(source);
        if (source is not null && toolSource is null) found.Add("--source is dockyard or package-manager");
        if (assets is not null && !Directory.Exists(assets)) found.Add("--assets needs a folder that exists");

        problems = found;
        var resume = flags.Contains("--resume");
        return new SetupRequest(with, without, toolSource, flags.Contains("--trust-https"), flags.Contains("--kubectl-shell"), flags.Contains("--silent") || resume, resume)
        {
            Start = !flags.Contains("--no-start"),
            Image = image,
            RemoveData = flags.Contains("--remove-data"),
            OpenDashboard = flags.Contains("--open-dashboard"),
            Assets = assets is null ? null : Path.GetFullPath(assets),
        };
    }

    static IEnumerable<string> Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
