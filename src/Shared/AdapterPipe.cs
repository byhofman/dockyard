using System.IO.Pipes;

namespace Dockyard.Shared;

// How the Toolbox asks the adapter for something: a line with the request, a line with the answer, on a pipe only this
// user can open. Requests: status, attach (take on an engine that already runs), start, stop, domains on, domains off.
static class AdapterPipe
{
    public static string Name => $"dockyard-adapter-{Environment.UserName}";
    public const string Ok = "ok";
    public const string Running = "running";
    public const string Stopped = "stopped";

    public static bool IsListening => LocalPipe.Exists(Name);

    // Null when no adapter answered.
    public static async Task<string?> Ask(string request, TimeSpan timeout)
    {
        if (!IsListening) return null;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, cts.Token);
            using var reader = new StreamReader(pipe);
            await using var writer = new StreamWriter(pipe) { AutoFlush = true };
            await writer.WriteLineAsync(request.AsMemory(), cts.Token);
            return await reader.ReadLineAsync(cts.Token);
        }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException) { return null; }
    }

    public static async Task Serve(Func<string, Task<string>> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(ct);
            _ = Task.Run(async () =>
            {
                await using (pipe)
                {
                    try
                    {
                        using var reader = new StreamReader(pipe);
                        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
                        if (await reader.ReadLineAsync(ct) is { } request) await writer.WriteLineAsync(await handle(request.Trim()));
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException) { }
                }
            }, ct);
        }
    }
}
