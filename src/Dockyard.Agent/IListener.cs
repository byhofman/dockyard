namespace Dockyard.Agent;

interface IListener : IDisposable
{
    Task<IConnection> AcceptAsync(CancellationToken ct);
}
