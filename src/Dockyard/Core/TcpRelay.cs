using System.Net;
using System.Net.Sockets;

namespace Dockyard.Core;

static class TcpRelay
{
    public static async Task Forward(Socket client, IPAddress address, int port)
    {
        using (client)
        using (var target = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp))
        {
            try
            {
                await target.ConnectAsync(address, port);
                await Task.WhenAll(Copy(client, target), Copy(target, client));
            }
            catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException) { }
        }
    }

    // A client done sending is passed on as a half-close, and what the engine still sends reaches it until the engine
    // ends. Disposing both afterwards is the caller's, which also ends the client's direction.
    public static async Task Carry(Stream client, Socket engine, CancellationToken ct)
    {
        var sending = Send();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await engine.ReceiveAsync(buffer, ct)) > 0)
        {
            await client.WriteAsync(buffer.AsMemory(0, n), ct);
            await client.FlushAsync(ct);
        }
        if (sending.IsCompleted) await sending;

        async Task Send()
        {
            var up = new byte[16 * 1024];
            try
            {
                int read;
                while ((read = await client.ReadAsync(up, ct)) > 0) await engine.SendAsync(up.AsMemory(0, read), ct);
                engine.Shutdown(SocketShutdown.Send);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
    }

    // One side finishing is passed on as a half-close, and the other direction keeps flowing until it ends too.
    static async Task Copy(Socket from, Socket to)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var n = await from.ReceiveAsync(buffer);
                if (n == 0) break;
                await to.SendAsync(buffer.AsMemory(0, n));
            }
        }
        catch (SocketException) { }
        try { to.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }
}
