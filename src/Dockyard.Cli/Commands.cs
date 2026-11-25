using System.Reflection;

namespace Dockyard.Cli;

static class Commands
{
    public const string Usage = """
        Usage: dockyard engine ls | start <engine> | stop <engine> | use <engine>
               dockyard config show | validate --file <file> | apply --file <file> | edit   [--engine <engine>]
               dockyard cluster ls
               dockyard cluster create <name> [--workers <n>] [--addons <names>] [--storage none|host-folder|nfs]
                                              [--no-load-balancer | --ports <ports>] [--file <definition>]
               dockyard cluster recreate <name> [--file <definition>]
               dockyard cluster delete | use | kubeconfig | definition <name>
               dockyard https on | off
               dockyard update [--check]
               dockyard version
               dockyard                     opens the Dockyard Toolbox
               dockyard --silent [...]      installs or changes Dockyard without a window (see the README)

          engine     The engines: linux, and win on Windows. use makes it the docker context.
          config     An engine's daemon.json: what you set in it, without the keys Dockyard adds (dns; hosts on Windows).
                     apply checks it, restarts the engine if it runs, and puts the previous one back if it will not start.
                     edit opens it in your editor (VISUAL or EDITOR; Notepad, TextEdit or nano otherwise) and applies it.
          cluster    kind clusters as the dashboard makes them: the context in your kubeconfig, the definition kept.
                     --addons: metrics-server, traefik (with Helm). --storage: shared storage every node uses at once,
                     a folder on the engine or NFS (with Helm). --ports: its load balancer's ports on 127.0.0.1, 80,443 when
                     not given. recreate deletes it, with everything in it, and makes it again from its definition.
          https      Trusted HTTPS for container domains, with Dockyard's own certificate authority.
          update     Brings the components up to date; --check only lists them. The Toolbox updates itself.
          --engine   linux or win (Windows); the active engine without it.
          --file     A file, or - for standard input.

        engine, config, https and update go to the Dockyard Toolbox, which owns the engines; cluster runs here.
        Exit code: 0 done, 1 failed, 2 wrong arguments.
        """;

    static readonly string[] ToolboxCommands = ["config", "engine", "https", "update"];

    public static string Version =>
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static int Main(string[] args)
    {
        try
        {
            return Run(args, Console.WriteLine, Console.Error.WriteLine).GetAwaiter().GetResult();
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    public enum Route { Help, Version, Cluster, ConfigEdit, Toolbox, HandOver }

    public static Route RouteOf(IReadOnlyList<string> args) => args switch
    {
        [] => Route.HandOver,
        ["help" or "--help" or "-h" or "/?", ..] => Route.Help,
        ["version" or "--version", ..] => Route.Version,
        ["cluster", ..] => Route.Cluster,
        ["config", "edit", ..] => Route.ConfigEdit,
        [var command, ..] when ToolboxCommands.Contains(command) => Route.Toolbox,
        [var option, ..] when option.StartsWith('-') => Route.HandOver,
        _ => throw new UsageException($"Unknown command: {string.Join(' ', args)}"),
    };

    public static async Task<int> Run(IReadOnlyList<string> args, Action<string> log, Action<string> error)
    {
        switch (RouteOf(args))
        {
            case Route.Help:
                log(Usage);
                return 0;
            case Route.Version:
                log(Version);
                return 0;
            case Route.Cluster:
                if (args.Count < 2) throw new UsageException("cluster needs a command: ls, create, recreate, delete, use, kubeconfig or definition");
                return await Clusters.Run(args[1], args.Skip(2).ToList(), log, error);
            case Route.ConfigEdit:
                return ConfigEdit.Run(args.Skip(2).ToList());
            case Route.Toolbox:
                return Toolbox.Run(args, log, error);
            default:
                return Toolbox.HandOver(args);
        }
    }
}
