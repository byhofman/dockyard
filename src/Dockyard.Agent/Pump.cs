using System.Net.Sockets;

namespace Dockyard.Agent;

static class Pump
{
    // Copies both ways. When one side stops sending, its end-of-stream is passed on (half-close) and the
    // other direction keeps flowing until it ends too: attach/exec and HTTP/1.0 clients rely on that.
    public static Task Both(IConnection a, IConnection b, CancellationToken ct) =>
        Task.WhenAll(Copy(a, b, ct), Copy(b, a, ct));

    // The same, with a copy of its own from a to b.
    public static Task Both(IConnection a, IConnection b, Func<Stream, Stream, Task> aToB, CancellationToken ct) =>
        Task.WhenAll(Copy(a, b, aToB, ct), Copy(b, a, ct));

    static Task Copy(IConnection from, IConnection to, CancellationToken ct) =>
        Copy(from, to, (f, t) => f.CopyToAsync(t, 64 * 1024, ct), ct);

    static async Task Copy(IConnection from, IConnection to, Func<Stream, Stream, Task> copy, CancellationToken ct)
    {
        try { await copy(from.Stream, to.Stream); }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException or OperationCanceledException or EndOfStreamException) { }
        to.ShutdownSend();
    }
}
