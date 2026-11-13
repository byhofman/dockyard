using System.Net;
using System.Net.Sockets;

namespace Dockyard.Agent;

sealed class TcpHostDialer(IPAddress host, IReadOnlySet<int>? refused = null) : IHostDialer
{
    public async Task<IConnection> Dial(int port, CancellationToken ct)
    {
        if (refused?.Contains(port) == true) throw new SocketException((int)SocketError.ConnectionRefused);
        var socket = new Socket(host.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return new SocketConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
