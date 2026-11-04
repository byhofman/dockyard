using System.IO.Pipes;

namespace Dockyard.Shared;

static class LocalPipe
{
    public static string TrayName => $"dockyard-tray-{Environment.UserName}";
    public static string DashboardName => $"dockyard-dashboard-{Environment.UserName}";

    // .NET maps named pipes to unix sockets named CoreFxPipe_<name> in the temp directory.
    public static bool Exists(string name) => OperatingSystem.IsWindows()
        ? File.Exists(@"\\.\pipe\" + name)
        : File.Exists(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + name));

    public static bool TrySend(string name, string line, int timeoutMs = 1000)
    {
        if (!Exists(name)) return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(line);
            writer.Flush();
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Serve(string name, Action<string> onLine)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe);
                    if (reader.ReadLine() is { } line) onLine(line.Trim());
                }
                catch (IOException) { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "Dockyard.Pipe." + name }.Start();
    }
}

enum TrayCommandKind { Show, Settings, Start, Switch }

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
