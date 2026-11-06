namespace Dockyard.Core;

sealed class SetupUsageException(string message) : Exception(message);

enum SetupAction { Install, Uninstall, Import }

sealed record SetupOptions
{
    public const string Usage = """
        Usage: Dockyard [--silent] [--with <names>] [--without <names>] [--no-start] [--image <file>]
               Dockyard --silent --uninstall [--remove-data]
               Dockyard --silent --import [--import-from <endpoint>]
               Dockyard --version

          --silent        No window: install, or update what is installed. Progress goes to the console.
          --with <names>     Add these components, comma separated, also on an existing install.
          --without <names>  Leave these out, or remove them from an existing install. Images stay unless --remove-data is given.
                             Names: dashboard, compose, buildx, linux-engine (Windows), windows-engine (Windows).
                             Without either: a new install gets everything, an existing install keeps what it has.
          --dashboard        The same as --with dashboard.
          --no-dashboard     The same as --without dashboard.
          --no-start      Do not start Dockyard afterwards.
          --image <file>  Windows: the Linux engine image to import instead of downloading it.
          --import        Copy images, volumes and containers from Docker Desktop, which keeps them.
          --import-from   The engine to copy from instead, as unix://, npipe:// or tcp://.
          --uninstall     Remove Dockyard. Images and containers stay unless --remove-data is given too.

        Exit code: 0 done, 3010 done after a reboot (Windows; setup resumes by itself), 2 wrong arguments, 1 failed.
        """;

    static readonly string[] WindowArguments = ["--setup", "--tray", "--settings"];
    static readonly string[] WindowArgumentsWithValue = ["--start", "--switch"];

    public bool Silent { get; init; }
    public SetupAction Action { get; init; }
    public bool? Dashboard { get; init; }
    public bool Start { get; init; } = true;
    public string? Image { get; init; }
    public bool RemoveData { get; init; }
    public bool Resume { get; init; }
    public bool OpenSetup { get; init; }
    public bool ShowVersion { get; init; }
    public bool ShowHelp { get; init; }
    public IReadOnlyList<string> With { get; init; } = [];
    public IReadOnlyList<string> Without { get; init; } = [];
    public string? ImportFrom { get; init; }

    public static SetupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new SetupOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            options = arg switch
            {
                "--silent" => options with { Silent = true },
                // --install is the name earlier one-liners and scripts use.
                "--install" => options with { Silent = true, Action = SetupAction.Install },
                "--uninstall" => options with { Silent = true, Action = SetupAction.Uninstall },
                "--import" => options with { Silent = true, Action = SetupAction.Import },
                "--import-from" => options with { Silent = true, Action = SetupAction.Import, ImportFrom = ++i < args.Count ? args[i] : throw new SetupUsageException("--import-from needs an endpoint") },
                "--dashboard" => options with { Dashboard = Choose(options.Dashboard, true) },
                "--no-dashboard" or "--engine-only" => options with { Dashboard = Choose(options.Dashboard, false) },
                "--with" => Names(options, ++i < args.Count ? args[i] : throw new SetupUsageException("--with needs names"), wanted: true),
                "--without" => Names(options, ++i < args.Count ? args[i] : throw new SetupUsageException("--without needs names"), wanted: false),
                "--no-start" => options with { Start = false },
                "--remove-data" => options with { RemoveData = true },
                "--resume" => options with { Resume = true },
                "--setup" => options with { OpenSetup = true },
                "--image" => options with { Image = ++i < args.Count ? args[i] : throw new SetupUsageException("--image needs a file") },
                "--version" => options with { ShowVersion = true },
                "--help" or "-h" => options with { ShowHelp = true },
                _ when WindowArguments.Contains(arg) => options,
                _ when WindowArgumentsWithValue.Contains(arg) => ++i < args.Count ? options : throw new SetupUsageException($"{arg} needs a value"),
                _ => throw new SetupUsageException($"Unknown argument: {arg}"),
            };
        }
        if (options.RemoveData && options.Action != SetupAction.Uninstall && options.Without.Count == 0)
            throw new SetupUsageException("--remove-data only goes with --uninstall or --without");
        if (options.With.Intersect(options.Without).FirstOrDefault() is { } both)
            throw new SetupUsageException($"{both} is in both --with and --without");
        return options;
    }

    static SetupOptions Names(SetupOptions options, string names, bool wanted)
    {
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ComponentRules.KnownFlags.Contains(name)) throw new SetupUsageException($"Unknown component: {name}. Names: {string.Join(", ", ComponentRules.KnownFlags)}");
            if (name == "dashboard") options = options with { Dashboard = Choose(options.Dashboard, wanted) };
            else options = wanted ? options with { With = [.. options.With, name] } : options with { Without = [.. options.Without, name] };
        }
        return options;
    }

    static bool Choose(bool? current, bool wanted) => current is null || current == wanted
        ? wanted
        : throw new SetupUsageException("--dashboard and --no-dashboard exclude each other");
}

static class DashboardChoice
{
    // A copy running from where it is installed was put there by an earlier script, which already chose the edition.
    public static bool Resolve(bool? requested, bool isInstalled, bool hasDashboard, bool runsFromInstallLocation) =>
        requested ?? (!isInstalled && !runsFromInstallLocation || hasDashboard);
}
