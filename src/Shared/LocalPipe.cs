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
