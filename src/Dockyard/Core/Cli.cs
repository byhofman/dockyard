using System.Diagnostics;
using Dockyard.Hosts;
using Dockyard.Shared;

namespace Dockyard.Core;

// dockyard <command>: what the dashboard and the tray do, for a terminal and for scripts. A command that changes an engine
// runs in the tray when it runs, since on Windows the tray holds the Linux engine's processes.
static class Cli
{
    public const string Usage = """
        Usage: dockyard engine ls | start <engine> | stop <engine> | use <engine>
               dockyard config show | validate --file <file> | apply --file <file> | edit   [--engine <engine>]
               dockyard cluster ls
               dockyard cluster create <name> [--workers <n>] [--addons <names>] [--shared-storage] [--file <definition>]
               dockyard cluster recreate <name> [--file <definition>]
               dockyard cluster delete | use | kubeconfig | definition <name>
               dockyard https on | off
               dockyard update [--check]

          engine     The engines: linux, and win on Windows. use makes it the docker context.
          config     An engine's daemon.json: what you set in it, without the keys Dockyard adds (dns; hosts on Windows).
                     apply checks it, restarts the engine if it runs, and puts the previous one back if it will not start.
                     edit opens it in your editor (VISUAL or EDITOR; Notepad, TextEdit or nano otherwise) and applies it.
          cluster    kind clusters as the dashboard makes them: the context in your kubeconfig, the definition kept.
                     --addons: metrics-server, traefik (with Helm). recreate deletes it, with everything in it, and makes
                     it again from its definition or the one given.
          https      Trusted HTTPS for container domains, with Dockyard's own certificate authority.
          update     Brings the components up to date; --check only lists them. Dockyard itself updates from the tray.
          --engine   linux or win (Windows); the active engine without it.
          --file     A file, or - for standard input.

        Exit code: 0 done, 1 failed, 2 wrong arguments.
        """;

    static readonly string[] Commands = ["config", "engine", "cluster", "https", "update"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Commands.Contains(args[0]);

    internal sealed class UsageException(string message) : Exception(message);

    // From a terminal: edit stays here, with the editor; everything else goes to the tray when there is one.
    public static int RunFromTerminal(string[] args)
    {
        try
        {
            if (args is ["config", "edit", ..]) return Edit(args);
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
                { Count: >= 2 } when args[0] == "cluster" => await CliClusters.Run(host, args[1], args.Skip(2).ToList(), log, error),
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
                log($"{(e.Id == active ? "*" : " ")} {e.Id}\t{e.Title}\t{(await host.IsRunning(e.Id) ? "running" : "stopped")}");
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

    // The file goes to the editor until it is valid or left unchanged.
    static int Edit(string[] args)
    {
        var host = HostFactory.Create();
        var engine = Engine(host, args);
        var current = host.ReadEngineSettings(engine).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"The {engine} engine's settings are not Dockyard's to edit");
        var file = Path.Combine(Path.GetTempPath(), $"dockyard-{engine}-daemon.json");
        File.WriteAllText(file, current);
        try
        {
            while (true)
            {
                OpenEditor(file);
                var edited = File.ReadAllText(file);
                if (edited == current)
                {
                    Console.WriteLine("No changes");
                    return 0;
                }
                if (host.ValidateEngineSettings(engine, edited).GetAwaiter().GetResult() is not { } problem)
                    return RunInTrayOrHere(["config", "apply", "--engine", engine, "--file", file]);
                Console.Error.WriteLine(problem);
                Console.Error.Write("Edit it again? [Y/n] ");
                if (Console.ReadLine()?.Trim().ToLowerInvariant() is "n" or "no") return 1;
            }
        }
        finally { File.Delete(file); }
    }

    static void OpenEditor(string file)
    {
        var editor = Environment.GetEnvironmentVariable("VISUAL") is { Length: > 0 } visual ? visual
            : Environment.GetEnvironmentVariable("EDITOR") is { Length: > 0 } set ? set
            : null;
        (string exe, string[] args) = editor is not null ? (editor, new[] { file })
            : OperatingSystem.IsWindows() ? ("notepad.exe", [file])
            : OperatingSystem.IsMacOS() ? ("/usr/bin/open", ["-W", "-t", file])
            : ("nano", [file]);
        // An editor given with options, as "code --wait", is run by the shell.
        if (editor is not null && editor.Contains(' '))
            (exe, args) = OperatingSystem.IsWindows() ? ("cmd.exe", new[] { "/c", $"{editor} \"{file}\"" }) : ("/bin/sh", new[] { "-c", $"{editor} {Shell.ShQuote(file)}" });
        using var process = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }) ?? throw new InvalidOperationException($"Could not start {exe}");
        process.WaitForExit();
    }
}
