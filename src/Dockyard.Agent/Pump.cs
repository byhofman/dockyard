using System.Net.Sockets;

namespace Dockyard.Agent;

static class Pump
{
    // Copies both ways. When one side stops sending, its end-of-stream is passed on (half-close) and the
    // other direction keeps flowing until it ends too: attach/exec and HTTP/1.0 clients rely on that.
    public static Task Both(IConnection a, IConnection b, CancellationToken ct) =>
        Task.WhenAll(Copy(a, b, ct), Copy(b, a, ct));

    static async Task Copy(IConnection from, IConnection to, CancellationToken ct)
    {
        try { await from.Stream.CopyToAsync(to.Stream, 64 * 1024, ct); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or OperationCanceledException) { }
        to.ShutdownSend();
    }
}
