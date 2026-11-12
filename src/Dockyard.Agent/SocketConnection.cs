using System.Net.Sockets;

namespace Dockyard.Agent;

sealed class SocketConnection(Socket socket) : IConnection
{
    readonly NetworkStream stream = new(socket, ownsSocket: true);
    public Stream Stream => stream;

    public void ShutdownSend()
    {
        try { socket.Shutdown(SocketShutdown.Send); }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
    }

    public void Dispose() => stream.Dispose();
}
