namespace Dockyard.Core;

static class AcceptLoop
{
    // The next instance exists before a connection is handled, so a client never finds the name missing. A client that
    // left before its connection was taken fails only that instance: ending the loop then would leave a name whose only
    // instances are busy, and docker waits for a free one without end.
    public static async Task Run<T>(T first, Func<T> create, Func<T, CancellationToken, Task> waitForClient, Action<T> handle, CancellationToken ct)
        where T : IDisposable
    {
        var waiting = first;
        try
        {
            while (true)
            {
                try
                {
                    await waitForClient(waiting, ct);
                }
                catch (IOException)
                {
                    waiting.Dispose();
                    waiting = create();
                    continue;
                }
                var connected = waiting;
                waiting = create();
                handle(connected);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or OperationCanceledException)
        {
            waiting.Dispose();
        }
    }
}
