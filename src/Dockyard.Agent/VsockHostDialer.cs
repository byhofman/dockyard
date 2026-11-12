using System.Text;

namespace Dockyard.Agent;

// A connection to the host's loopback, from the engine's Linux.
sealed class VsockHostDialer : IHostDialer
{
    const uint HostCid = 2;

    public async Task<IConnection> Dial(int port, CancellationToken ct)
    {
        var connection = await VsockTransport.Connect(HostCid, GuestAddresses.HostProxyPort, ct);
        try
        {
            await connection.Stream.WriteAsync(Encoding.ASCII.GetBytes($"tcp {port}\n"), ct);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
