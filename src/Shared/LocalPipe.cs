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

    const string ReplyPrefix = "dockyard-reply-";
    public const string ExitLine = "\u0001exit ";

    public static bool IsReplyName(string name) => name.StartsWith(ReplyPrefix, StringComparison.Ordinal) && name.Length == ReplyPrefix.Length + 32
        && name[ReplyPrefix.Length..].All(Uri.IsHexDigit);

    // Asks the tray to run a dockyard command and passes on what it reports. Null when no tray answered, so the caller runs
    // the command itself.
    public static int? RunInTray(IReadOnlyList<string> args, Action<string> onLine, int connectTimeoutMs = 5000)
    {
        if (!Exists(TrayName)) return null;
        var reply = ReplyPrefix + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(reply, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
        if (!TrySend(TrayName, new TrayCommand(TrayCommandKind.Run, Reply: reply, Args: args).ToString())) return null;
        try { server.WaitForConnectionAsync().Wait(connectTimeoutMs); }
        catch (AggregateException) { return null; }
        if (!server.IsConnected) return null;
        using var reader = new StreamReader(server);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith(ExitLine, StringComparison.Ordinal)) return int.TryParse(line[ExitLine.Length..], out var code) ? code : 1;
            onLine(line);
        }
        return 1;
    }

    // The tray's end of a reply pipe: a line per call, and the exit code last.
    public static StreamWriter OpenReply(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        pipe.Connect(5000);
        return new StreamWriter(pipe) { AutoFlush = true };
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
