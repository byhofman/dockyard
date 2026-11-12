namespace Dockyard.Agent;

interface ITransport
{
    IListener Listen(int port);
}
