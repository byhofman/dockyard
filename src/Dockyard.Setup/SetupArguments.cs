using Dockyard.Core;

namespace Dockyard.Setup;

public enum AppAction { Setup, Update, Add, Remove, Uninstall }

// Components: what Update, Add and Remove name; none for Update is everything with an update.
public sealed record AppCommand(AppAction Action, SetupRequest Setup, IReadOnlyList<string> Components, bool RemoveData = false);

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

    static readonly string[] Actions = ["--setup", "--update", "--component", "--uninstall"];
    const string ComponentUsage = "--component is add or remove, then the component";

    // The dashboard's headless actions, which mirror the CLI's setup, update, component add|remove and uninstall. Null when
    // none is asked for: the window opens. --silent goes with each, since only setup has a way to ask.
    public static AppCommand? ParseCommand(IReadOnlyList<string> args, out IReadOnlyList<string> problems)
    {
        var at = args.ToList().FindIndex(a => Actions.Contains(a));
        problems = [];
        if (at < 0) return null;
        List<string> rest = [.. args.Take(at), .. args.Skip(at + 1)];
        var none = Parse([], out _);
        switch (args[at])
        {
            case "--setup":
                return new AppCommand(AppAction.Setup, Parse(rest, out problems), []);
            case "--update":
            {
                var found = Unknown(rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)), "--silent");
                problems = found;
                return new AppCommand(AppAction.Update, none, [.. rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).SelectMany(Split)]);
            }
            case "--component":
            {
                var words = rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
                if (words is not ["add" or "remove", _, ..]) { problems = [ComponentUsage]; return new AppCommand(AppAction.Add, none, []); }
                var remove = words[0] == "remove";
                var found = Unknown(rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)), remove ? ["--silent", "--remove-data"] : ["--silent"]);
                found.AddRange(words.Skip(2).Select(w => $"Unexpected argument {w}"));
                problems = found;
                return new AppCommand(remove ? AppAction.Remove : AppAction.Add, none, [words[1]], rest.Contains("--remove-data"));
            }
            default:
            {
                var found = Unknown(rest.Where(a => a.StartsWith("--", StringComparison.Ordinal)), "--silent", "--remove-data");
                found.AddRange(rest.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(w => $"Unexpected argument {w}"));
                problems = found;
                return new AppCommand(AppAction.Uninstall, none, [], rest.Contains("--remove-data"));
            }
        }
    }

    static List<string> Unknown(IEnumerable<string> options, params string[] allowed) =>
        [.. options.Where(o => !allowed.Contains(o)).Select(o => $"Unknown option {o}")];

    // A step handed over, written as the app that finishes it reads it: the CLI's verb, or the dashboard's option for it.
    internal static IReadOnlyList<string> ForApp(RunningApp app, IReadOnlyList<string> verbFirst) =>
        app == RunningApp.Dashboard ? ["--" + verbFirst[0], .. verbFirst.Skip(1)] : verbFirst;
}
