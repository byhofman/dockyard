namespace Dockyard.Agent;

interface IConnection : IDisposable
{
    Stream Stream { get; }
    void ShutdownSend();
}
