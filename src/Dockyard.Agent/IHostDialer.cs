namespace Dockyard.Agent;

interface IHostDialer
{
    Task<IConnection> Dial(int port, CancellationToken ct);
}
