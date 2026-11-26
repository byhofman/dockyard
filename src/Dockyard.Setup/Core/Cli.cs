using System.Diagnostics;
using Dockyard.Setup;
using Dockyard.Shared;

namespace Dockyard.Core;

// dockyard <command>: what the dashboard and the tray do, for a terminal and for scripts. A command that changes an engine
// runs in the tray when it runs, since on Windows the tray holds the Linux engine's processes.
static class Cli
{
    public const string Usage = "Usage: dockyard engine | config | https | update ... (dockyard --help lists them all)";

    static readonly string[] Commands = ["config", "engine", "https", "update"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    internal sealed class UsageException(string message) : Exception(message);

    // From a terminal: edit stays here, with the editor; everything else goes to the tray when there is one.
    public static int RunFromTerminal(string[] args)
    {
        try
        {
            return RunInTrayOrHere(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
    }

    static int RunInTrayOrHere(IReadOnlyList<string> args)
    {
        // The file is read here: the tray runs as the same user, but stdin is this process's.
        args = InlineFile(args);
        if (LocalPipe.RunInTray(args, Console.WriteLine) is { } code) return code;
        return Execute(DockyardSetup.ForThisMachine(), args, Console.WriteLine, Console.Error.WriteLine).GetAwaiter().GetResult();
    }

    const string InlinePrefix = "inline:";

    static IReadOnlyList<string> InlineFile(IReadOnlyList<string> args)
    {
        var list = args.ToList();
        var at = list.IndexOf("--file");
        if (at < 0 || at + 1 >= list.Count) return list;
        var file = list[at + 1];
        list[at + 1] = InlinePrefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(file == "-" ? Console.In.ReadToEnd() : File.ReadAllText(file)));
        return list;
    }

    public static async Task<int> Execute(DockyardSetup setup, IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        try
        {
            return args switch
            {
                { Count: >= 2 } when args[0] == "config" => await Config(setup, args[1], args.Skip(2).ToList(), log, error),
                { Count: >= 2 } when args[0] == "engine" => await Engines(setup, args[1], args.Skip(2).ToList(), log),
                { Count: 3 } when args[0] == "https" && args[1] == "cluster-issuer" => await ClusterIssuer(setup, args[2], log),
                { Count: 2 } when args[0] == "https" => await HttpsCommand(setup, args[1], log),
                { Count: >= 1 } when args[0] == "update" => await Update(setup, args.Contains("--check"), log, error),
                _ => throw new UsageException($"Unknown command: {string.Join(' ', args)}"),
            };
        }
        catch (Exception e) when (e is UsageException or ArgumentException)
        {
            error(e.Message);
            error(Usage);
            return 2;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            error(e.Message);
            return 1;
        }
    }

    static IProgress<SetupProgress> Progress(Action<string> log) => new ReportTo(p => log(p.Line));

    internal static string? Option(IReadOnlyList<string> args, string name)
    {
        var at = args.ToList().IndexOf(name);
        if (at < 0) return null;
        return at + 1 < args.Count ? args[at + 1] : throw new UsageException($"{name} needs a value");
    }

    static string Engine(DockyardSetup setup, IReadOnlyList<string> args)
    {
        var engine = Option(args, "--engine") ?? setup.ActiveEngine();
        return setup.Engines.Any(e => e.Id == engine) ? engine : throw new UsageException($"No engine {engine} here. Engines: {string.Join(", ", setup.Engines.Select(e => e.Id))}");
    }

    internal static string Content(IReadOnlyList<string> args)
    {
        var file = Option(args, "--file") ?? throw new UsageException("--file is required");
        return file.StartsWith(InlinePrefix, StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file[InlinePrefix.Length..]))
            : File.ReadAllText(file);
    }

    static async Task<int> Config(DockyardSetup setup, string verb, IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        var key = SettingDefinitions.EngineFilePrefix + Engine(setup, args);
        switch (verb)
        {
            case "show":
                log(setup.GetSetting(key));
                return 0;
            case "validate":
            {
                var json = Content(args);
                if (await setup.CheckSetting(key, json) is { } problem) { error(problem); return 1; }
                foreach (var overridden in EngineSettings.Overridden(key[SettingDefinitions.EngineFilePrefix.Length..], json)) log($"{overridden}: Dockyard sets this one itself, so yours is replaced");
                log("The engine accepts these settings");
                return 0;
            }
            case "apply":
                await setup.SetSetting(key, Content(args), Progress(log));
                return 0;
            default: throw new UsageException($"Unknown config command: {verb}");
        }
    }

    static async Task<int> Engines(DockyardSetup setup, string verb, IReadOnlyList<string> args, Action<string> log)
    {
        if (verb == "ls")
        {
            var active = setup.ActiveEngine();
            foreach (var e in setup.Engines)
                log($"{(e.Id == active ? "*" : " ")} {e.Id}\t{e.Title}\t{(await setup.IsAnswering(e.Id) ? "running" : "stopped")}");
            return 0;
        }
        var engine = args.FirstOrDefault() is { } id && setup.Engines.Any(e => e.Id == id)
            ? id : throw new UsageException($"An engine is needed: {string.Join(", ", setup.Engines.Select(e => e.Id))}");
        switch (verb)
        {
            case "start": await setup.StartEngine(engine); log($"{engine} runs"); break;
            case "stop": await setup.StopEngine(engine); log($"{engine} is stopped"); break;
            case "use": await setup.UseEngine(engine); log($"docker uses {engine}"); break;
            default: throw new UsageException($"Unknown engine command: {verb}");
        }
        return 0;
    }

    static async Task<int> HttpsCommand(DockyardSetup setup, string state, Action<string> log)
    {
        if (state is not ("on" or "off")) throw new UsageException("https takes on or off");
        await setup.SetSetting(SettingDefinitions.TrustedHttps, state, Progress(log));
        return 0;
    }

    // Asked for by the dashboard and the command when they make a cluster with cert-manager.
    static async Task<int> ClusterIssuer(DockyardSetup setup, string cluster, Action<string> log)
    {
        foreach (var line in (await setup.ClusterCa(cluster)).TrimEnd().Split('\n')) log(line);
        return 0;
    }

    static async Task<int> Update(DockyardSetup setup, bool checkOnly, Action<string> log, Action<string> error)
    {
        var checks = await setup.CheckUpdates(CancellationToken.None);
        foreach (var check in checks)
        {
            if (check.Error is { } problem) { error($"{check.Row.Title}: {problem}"); continue; }
            if (check.Row.Installed is not { } installed) continue;
            log($"{check.Row.Title}\t{installed}\t{(check.Row.CanUpdate ? $"{check.Row.Newest} available" : "up to date")}");
        }
        if (!checkOnly) await setup.UpdateAvailable(checks, Progress(log));
        return 0;
    }
}
