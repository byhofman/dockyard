using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

sealed class TcpTransport : ITransport
{
    public IListener Listen(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.Listen(64);
        return new Listener(socket);
    }

    sealed class Listener(Socket socket) : IListener
    {
        public async Task<IConnection> AcceptAsync(CancellationToken ct) => new SocketConnection(await socket.AcceptAsync(ct));
        public void Dispose() => socket.Dispose();
    }
}
