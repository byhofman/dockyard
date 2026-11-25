using System.Diagnostics;
using Dockyard.Hosts;
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
        return Execute(HostFactory.Create(), args, Console.WriteLine, Console.Error.WriteLine).GetAwaiter().GetResult();
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

    public static async Task<int> Execute(IEngineHost host, IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        try
        {
            return args switch
            {
                { Count: >= 2 } when args[0] == "config" => await Config(host, args[1], args.Skip(2).ToList(), log, error),
                { Count: >= 2 } when args[0] == "engine" => await Engines(host, args[1], args.Skip(2).ToList(), log),
                { Count: 2 } when args[0] == "https" => await HttpsCommand(host, args[1], log),
                { Count: >= 1 } when args[0] == "update" => await Update(host, args.Contains("--check"), log, error),
                _ => throw new UsageException($"Unknown command: {string.Join(' ', args)}"),
            };
        }
        catch (UsageException e)
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

    internal static string? Option(IReadOnlyList<string> args, string name)
    {
        var at = args.ToList().IndexOf(name);
        if (at < 0) return null;
        return at + 1 < args.Count ? args[at + 1] : throw new UsageException($"{name} needs a value");
    }

    static string Engine(IEngineHost host, IReadOnlyList<string> args)
    {
        var engine = Option(args, "--engine") ?? host.ActiveEngine();
        return host.Engines.Any(e => e.Id == engine) ? engine : throw new UsageException($"No engine {engine} here. Engines: {string.Join(", ", host.Engines.Select(e => e.Id))}");
    }

    internal static string Content(IReadOnlyList<string> args)
    {
        var file = Option(args, "--file") ?? throw new UsageException("--file is required");
        return file.StartsWith(InlinePrefix, StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(file[InlinePrefix.Length..]))
            : File.ReadAllText(file);
    }

    static async Task<int> Config(IEngineHost host, string verb, IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        var engine = Engine(host, args);
        switch (verb)
        {
            case "show":
                log(await host.ReadEngineSettings(engine) ?? throw new InvalidOperationException($"The {engine} engine's settings are not Dockyard's to show"));
                return 0;
            case "validate":
            {
                var json = Content(args);
                if (await host.ValidateEngineSettings(engine, json) is { } problem) { error(problem); return 1; }
                foreach (var key in EngineSettings.Overridden(engine, json)) log($"{key}: Dockyard sets this one itself, so yours is replaced");
                log("The engine accepts these settings");
                return 0;
            }
            case "apply":
            {
                var json = Content(args);
                if (await host.ValidateEngineSettings(engine, json) is { } problem) { error(problem); return 1; }
                foreach (var key in EngineSettings.Overridden(engine, json)) log($"{key}: Dockyard sets this one itself, so yours is replaced");
                await host.ApplyEngineSettings(log, engine, json);
                return 0;
            }
            default: throw new UsageException($"Unknown config command: {verb}");
        }
    }

    static async Task<int> Engines(IEngineHost host, string verb, IReadOnlyList<string> args, Action<string> log)
    {
        if (verb == "ls")
        {
            var active = host.ActiveEngine();
            foreach (var e in host.Engines)
                log($"{(e.Id == active ? "*" : " ")} {e.Id}\t{e.Title}\t{(await host.IsRunning(e.Id) || await Answers(e.Endpoint) ? "running" : "stopped")}");
            return 0;
        }
        var engine = args.FirstOrDefault() is { } id && host.Engines.Any(e => e.Id == id)
            ? id : throw new UsageException($"An engine is needed: {string.Join(", ", host.Engines.Select(e => e.Id))}");
        switch (verb)
        {
            case "start": await host.Start(engine); log($"{engine} runs"); break;
            case "stop": await host.Stop(engine); log($"{engine} is stopped"); break;
            case "use": await host.UseContext(engine); log($"docker uses {engine}"); break;
            default: throw new UsageException($"Unknown engine command: {verb}");
        }
        return 0;
    }

    // Outside the tray, which holds the Linux engine's relay on Windows, the engine runs when its endpoint answers.
    static async Task<bool> Answers(string endpoint)
    {
        try
        {
            using var client = new Dockyard.Engine.DockerClient(Dockyard.Engine.Transport.DockerEndpoint.Parse(endpoint));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await client.PingAsync(timeout.Token);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or TimeoutException or Dockyard.Engine.EngineUnavailableException) { return false; }
    }

    static async Task<int> HttpsCommand(IEngineHost host, string state, Action<string> log)
    {
        switch (state)
        {
            case "on": await Https.Enable(host, log, silent: false); break;
            case "off": await Https.Disable(host, log, silent: false); break;
            default: throw new UsageException("https takes on or off");
        }
        return 0;
    }

    // Dockyard itself is left to the tray, which has to restart for it.
    static async Task<int> Update(IEngineHost host, bool checkOnly, Action<string> log, Action<string> error)
    {
        List<(string Component, Release? Release)> available = [];
        foreach (var c in host.Components)
        {
            Version? installed;
            Release? latest;
            try
            {
                installed = await host.Installed(c.Id);
                latest = installed is null ? null : await host.Latest(c.Id);
            }
            catch (HttpRequestException e)
            {
                error($"{c.Title}: {e.Message}");
                continue;
            }
            if (installed is null) continue;
            var newer = latest is not null && latest.Version > installed;
            log($"{c.Title}\t{installed}\t{(newer ? $"{latest!.Version} available" : "up to date")}");
            if (newer && c.Id != nameof(Component.App)) available.Add((c.Id, latest));
        }
        if (checkOnly || available.Count == 0) return 0;
        await host.Update(log, available);
        return 0;
    }

}
